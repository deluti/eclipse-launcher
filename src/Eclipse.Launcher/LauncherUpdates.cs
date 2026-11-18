using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Eclipse;

internal sealed record LauncherRelease(Version Version, string Tag, Uri Installer, long Size, string Sha256);

internal sealed class LauncherUpdates(HttpClient http)
{
    internal const string Repository = "deluti/eclipse-launcher";
    internal const string InstallerName = "eclipse-launcher-windows-x86.exe";
    internal static Version CurrentVersion => Normalize(typeof(LauncherUpdates).Assembly.GetName().Version!);
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    internal async Task<LauncherRelease?> CheckAsync(CancellationToken ct, Version? currentVersion = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + "/releases/latest");
        request.Headers.UserAgent.ParseAdd("EclipseLauncher/" + CurrentVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response.Content, 2 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;
        var tag = root.Text("tag_name");
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || versionText.Split('.').Length != 3 || version <= Normalize(currentVersion ?? CurrentVersion)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var installer = assets.FirstOrDefault(a => a.Text("name") == InstallerName);
        if (installer.ValueKind == JsonValueKind.Undefined) return null;
        var url = ValidatedAssetUrl(installer.Text("browser_download_url"), tag, InstallerName);
        var size = installer.GetProperty("size").GetInt64();
        if (size <= 0 || size > 512L * 1024 * 1024) throw new InvalidDataException(L.T("Недопустимый размер обновления."));
        var hash = installer.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String ? digest.GetString() : null;
        if (hash?.StartsWith("sha256:", StringComparison.Ordinal) == true) hash = hash[7..]; else hash = null;
        if (hash == null)
        {
            var checksums = assets.FirstOrDefault(a => a.Text("name") == "SHA256SUMS.txt");
            if (checksums.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException(L.T("В релизе отсутствует контрольная сумма обновления."));
            var checksumUrl = ValidatedAssetUrl(checksums.Text("browser_download_url"), tag, "SHA256SUMS.txt");
            using var checksumResponse = await http.GetAsync(checksumUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            checksumResponse.EnsureSuccessStatusCode();
            var text = System.Text.Encoding.UTF8.GetString(await ReadBoundedAsync(checksumResponse.Content, 65536, timeout.Token).ConfigureAwait(false));
            foreach (var line in text.Split('\n'))
            {
                var pair = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (pair.Length == 2 && pair[1] == InstallerName) { hash = pair[0]; break; }
            }
        }
        if (hash?.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException(L.T("Недопустимая SHA-256 обновления."));
        return new LauncherRelease(Normalize(version), tag, url, size, hash.ToLowerInvariant());
    }
    private static Uri ValidatedAssetUrl(string url, string tag, string name)
    {
        var expected = "https://github.com/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + name;
        if (url != expected) throw new InvalidDataException(L.T("Обновление должно находиться в официальном репозитории Eclipse."));
        return new Uri(url);
    }
    internal async Task<string> DownloadAsync(LauncherRelease release, string directory, IProgress<int> progress, CancellationToken ct)
    {
        _ = ValidatedAssetUrl(release.Installer.AbsoluteUri, release.Tag, InstallerName);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, InstallerName);
        var temporary = target + ".part";
        try
        {
            using var response = await http.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new InvalidDataException(L.T("Размер обновления не совпал."));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long received = 0; var percentage = -1;
                while (await source.ReadAsync(buffer, ct).ConfigureAwait(false) is var count && count != 0)
                {
                    received += count;
                    if (received > release.Size) throw new InvalidDataException(L.T("Размер обновления превышен."));
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    var next = (int)(received * 100 / release.Size);
                    if (next != percentage) { percentage = next; progress.Report(next); }
                }
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.T("Контрольная сумма обновления не совпала. Установка отменена."));
            }
            File.Move(temporary, target, true);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static ProcessStartInfo InstallerStartInfo(string installer, string appDirectory, string dataDirectory, int parentId)
    {
        var info = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/ECLIPSEUPDATE=1", "/LAUNCHERPID=" + parentId,
            "/DIR=" + Path.GetFullPath(appDirectory), "/ECLIPSEDATA=" + Path.GetFullPath(dataDirectory) }) info.ArgumentList.Add(argument);
        return info;
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var target = new MemoryStream(); var buffer = new byte[16384];
        while (await source.ReadAsync(buffer, ct).ConfigureAwait(false) is var count && count != 0)
        {
            if (target.Length + count > maximum) throw new InvalidDataException(L.T("Ответ сервера обновлений слишком большой."));
            target.Write(buffer, 0, count);
        }
        return target.ToArray();
    }
}
