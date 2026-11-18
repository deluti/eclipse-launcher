using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Eclipse;

public sealed record DownloadFile(string Url, string RelativePath, string Sha1, long Size);
public sealed record FileReceipt(string Sha1, long Size, long Modified);

public sealed class Downloads
{
    private readonly HttpClient http;
    private readonly AppPaths paths;
    private readonly ConcurrentDictionary<string, FileReceipt> receipts;
    public Downloads(HttpClient http, AppPaths paths)
    {
        this.http = http;
        this.paths = paths;
        Dictionary<string, FileReceipt>? saved = null;
        try { saved = JsonStore.Read<Dictionary<string, FileReceipt>>(paths.FilePath("verified-files.json")); }
        catch (JsonException) { }
        receipts = new(saved ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private string ReceiptPath => paths.FilePath("verified-files.json");
    public void SaveReceipts() => JsonStore.Write(ReceiptPath, receipts);

    public async Task EnsureAllAsync(IEnumerable<DownloadFile> files, IProgress<string> progress, CancellationToken ct)
    {
        var unique = files.DistinctBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var done = 0;
        long lastReport = 0;
        try
        {
            await Parallel.ForEachAsync(unique, new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct }, async (file, token) =>
            {
                await EnsureAsync(file, token).ConfigureAwait(false);
                var count = Interlocked.Increment(ref done);
                var now = Environment.TickCount64;
                var previous = Interlocked.Read(ref lastReport);
                if (now - previous > 120 && Interlocked.CompareExchange(ref lastReport, now, previous) == previous)
                    progress.Report(L.F("Подготовка файлов: {0:N0} / {1:N0}", count, unique.Length));
            }).ConfigureAwait(false);
        }
        finally { SaveReceipts(); }
    }

    public async Task EnsureAsync(DownloadFile file, CancellationToken ct)
    {
        Validate(file);
        var target = AppPaths.SafeChild(paths.Root, file.RelativePath);
        if (File.Exists(target))
        {
            var info = new FileInfo(target);
            if (info.Length == file.Size)
            {
                if (receipts.TryGetValue(file.RelativePath, out var receipt)
                    && receipt.Sha1.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase)
                    && receipt.Size == info.Length && receipt.Modified == info.LastWriteTimeUtc.Ticks) return;
                if (await MatchesAsync(target, file.Sha1, ct).ConfigureAwait(false)) { Remember(file, target); return; }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".part";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                if (new FileInfo(temporary).Length != file.Size || !await MatchesAsync(temporary, file.Sha1, ct).ConfigureAwait(false))
                    throw new InvalidDataException(L.T("Контрольная сумма загрузки не совпала: ") + Path.GetFileName(target));
                File.Move(temporary, target, true);
                Remember(file, target);
                return;
            }
            catch (Exception ex) when (attempt < 2 && (ex is HttpRequestException or IOException))
            { await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), ct).ConfigureAwait(false); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static void Validate(DownloadFile file)
    {
        var uri = new Uri(file.Url);
        string[] hosts = ["piston-meta.mojang.com", "piston-data.mojang.com", "launchermeta.mojang.com", "launcher.mojang.com", "libraries.minecraft.net", "resources.download.minecraft.net", "maven.fabricmc.net", "maven.quiltmc.org", "repo.maven.apache.org"];
        if (uri.Scheme != "https" || !hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || file.Size < 0 || file.Sha1.Length != 40 || file.Sha1.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException(L.T("Недопустимый источник или контрольная сумма файла Minecraft."));
    }

    private void Remember(DownloadFile file, string target)
    {
        var info = new FileInfo(target);
        receipts[file.RelativePath] = new(file.Sha1, info.Length, info.LastWriteTimeUtc.Ticks);
    }
    private static async Task<bool> MatchesAsync(string path, string sha1, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }
}
