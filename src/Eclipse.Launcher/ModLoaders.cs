using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eclipse;

public sealed record ModdedProfile(string Id, string GameVersion, string Loader, string LoaderVersion)
{
    public string Name { get; init; } = "";
    public string SourceVersion { get; init; } = "";
    public bool SharedDirectory { get; init; }
    public string GameDirectory => SharedDirectory ? "" : "instances/" + Id;
    public override string ToString() => !string.IsNullOrEmpty(Name) ? Name : Loader == "vanilla" ? GameVersion : $"{GameVersion} · {(Loader == "fabric" ? "Fabric" : "Quilt")} {LoaderVersion}";
    internal static List<ModdedProfile> ReadAll(AppPaths paths)
    {
        try { return JsonStore.Read<List<ModdedProfile>>(paths.FilePath("profiles.json")) ?? []; }
        catch (JsonException) { return []; }
    }
}

public sealed record LoaderVersion(string Version, bool Stable)
{
    public override string ToString() => Version;
}

public sealed class ModLoaders(HttpClient http, AppPaths paths, Minecraft minecraft)
{
    private static string Api(string loader) => loader switch
    {
        "fabric" => "https://meta.fabricmc.net/v2/versions/loader/",
        "quilt" => "https://meta.quiltmc.org/v3/versions/loader/",
        _ => throw new InvalidOperationException(L.T("Неизвестный загрузчик"))
    };
    internal static void ValidateVersion(string value)
    {
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.+\-]{0,100}\z")) throw new InvalidDataException(L.T("Недопустимая версия загрузчика или Minecraft"));
    }
    public async Task<IReadOnlyList<LoaderVersion>> VersionsAsync(string loader, string gameVersion, CancellationToken ct)
    {
        ValidateVersion(gameVersion);
        var cache = AppPaths.SafeChild(paths.Root, $"loader-catalog/{loader}-{gameVersion}.json");
        JsonElement json;
        try
        {
            json = await Network.JsonAsync(http, HttpMethod.Get, Api(loader) + Uri.EscapeDataString(gameVersion), null, null, ct).ConfigureAwait(false);
            JsonStore.Write(cache, json);
        }
        catch (Exception ex) when ((ex is HttpRequestException or ServiceException) && File.Exists(cache))
        { json = JsonStore.Read<JsonElement>(cache); }
        var result = new List<LoaderVersion>();
        foreach (var item in json.EnumerateArray())
        {
            var info = item.GetProperty("loader");
            var version = info.Text("version"); ValidateVersion(version);
            var stable = info.TryGetProperty("stable", out var flag) ? flag.GetBoolean() : !Regex.IsMatch(version, "alpha|beta|rc|snapshot", RegexOptions.IgnoreCase);
            result.Add(new(version, stable));
        }
        return result.DistinctBy(v => v.Version).OrderByDescending(v => v.Stable).ThenByDescending(v => NumericVersion(v.Version)).ToArray();
    }
    private static Version NumericVersion(string value) => Version.TryParse(value.Split('-', '+')[0], out var version) ? version : new Version(0, 0);

    public async Task<InstallPlan> InstallAsync(string loader, string gameVersion, string loaderVersion, IProgress<string> progress, CancellationToken ct)
    {
        ValidateVersion(gameVersion); ValidateVersion(loaderVersion);
        var api = Api(loader);
        var profileId = $"{loader}-loader-{loaderVersion}-{gameVersion}";
        var planPath = AppPaths.SafeChild(paths.Root, $"plans/{profileId}.json");
        InstallPlan? saved = null;
        try { saved = JsonStore.Read<InstallPlan>(planPath); } catch (JsonException) { }
        if (saved is { Format: 1 } && saved.Version == profileId)
        {
            var ready = await minecraft.PrepareAsync(profileId, progress, ct).ConfigureAwait(false);
            SaveProfile(new(profileId, gameVersion, loader, loaderVersion));
            return ready;
        }
        progress.Report(L.F("Получение профиля {0} {1}…", loader, loaderVersion));
        var metadata = await Network.JsonAsync(http, HttpMethod.Get,
            api + Uri.EscapeDataString(gameVersion) + "/" + Uri.EscapeDataString(loaderVersion) + "/profile/json", null, null, ct).ConfigureAwait(false);
        if (metadata.Text("inheritsFrom") != gameVersion || metadata.Text("id") != profileId)
            throw new InvalidDataException(L.T("Профиль загрузчика относится к другой версии Minecraft"));
        var mainClass = metadata.Text("mainClass");
        var prefix = loader == "fabric" ? "net.fabricmc.loader." : "org.quiltmc.loader.";
        if (!mainClass.StartsWith(prefix, StringComparison.Ordinal) || !mainClass.EndsWith(".KnotClient", StringComparison.Ordinal))
            throw new InvalidDataException(L.T("Неизвестная точка запуска загрузчика"));
        progress.Report(L.F("Подготовка Minecraft {0}…", gameVersion));
        var basis = await minecraft.PrepareAsync(gameVersion, progress, ct).ConfigureAwait(false);
        var plan = new InstallPlan
        {
            Version = profileId, BaseVersion = gameVersion, GameRelativeDirectory = "instances/" + profileId,
            JavaRelativePath = basis.JavaRelativePath, JavaMajorVersion = basis.JavaMajorVersion, AssetIndex = basis.AssetIndex, MainClass = mainClass,
            NativeJars = [.. basis.NativeJars], NativeFiles = [.. basis.NativeFiles], Files = [.. basis.Files], RuntimeLinks = [.. basis.RuntimeLinks],
            JvmArguments = [.. basis.JvmArguments], GameArguments = [.. basis.GameArguments]
        };
        if (metadata.TryGetProperty("arguments", out var arguments))
        {
            if (arguments.TryGetProperty("jvm", out var jvm)) plan.JvmArguments.AddRange(Minecraft.SelectArguments(jvm));
            if (arguments.TryGetProperty("game", out var game)) plan.GameArguments.AddRange(Minecraft.SelectArguments(game));
        }
        var replacements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in metadata.GetProperty("libraries").EnumerateArray())
        {
            if (!Minecraft.Allowed(library)) continue;
            var file = await ResolveLibraryAsync(library, ct).ConfigureAwait(false);
            plan.Files.Add(file); plan.ClassPath.Add(file.RelativePath); replacements.Add(ClassPathKey(file.RelativePath));
        }
        plan.ClassPath.AddRange(basis.ClassPath.Where(path => !replacements.Contains(ClassPathKey(path))));
        progress.Report(L.T("Установка библиотек загрузчика…"));
        await new Downloads(http, paths).EnsureAllAsync(plan.Files, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var versionFile = AppPaths.SafeChild(paths.Root, $"game/versions/{profileId}/{profileId}.json");
        JsonStore.Write(versionFile, metadata);
        Directory.CreateDirectory(AppPaths.SafeChild(paths.Root, plan.GameRelativeDirectory + "/mods"));
        JsonStore.Write(planPath, plan);
        SaveProfile(new(profileId, gameVersion, loader, loaderVersion));
        return plan;
    }
    private void SaveProfile(ModdedProfile profile)
    {
        var profiles = ModdedProfile.ReadAll(paths);
        if (profiles.FirstOrDefault(p => p.Id == profile.Id) is { } old) profile = profile with { Name = old.Name };
        profiles.RemoveAll(p => p.Id == profile.Id); profiles.Insert(0, profile);
        JsonStore.Write(paths.FilePath("profiles.json"), profiles);
    }
    internal static string MavenPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(p => !Regex.IsMatch(p, @"\A[A-Za-z0-9_][A-Za-z0-9_.+\-]*\z")))
            throw new InvalidDataException(L.T("Некорректные координаты библиотеки загрузчика"));
        var classifier = parts.Length == 4 ? "-" + parts[3] : "";
        return parts[0].Replace('.', '/') + "/" + parts[1] + "/" + parts[2] + "/" + parts[1] + "-" + parts[2] + classifier + ".jar";
    }
    private static string ClassPathKey(string path)
    {
        if (!path.StartsWith("game/libraries/", StringComparison.Ordinal)) return path;
        var last = path.LastIndexOf('/');
        return last > 0 ? path[..path.LastIndexOf('/', last - 1)] : path;
    }
    private async Task<DownloadFile> ResolveLibraryAsync(JsonElement library, CancellationToken ct)
    {
        var path = MavenPath(library.Text("name"));
        var repository = new Uri(library.Text("url"));
        if (repository.Scheme != "https" || repository.Host is not ("maven.fabricmc.net" or "maven.quiltmc.org" or "repo.maven.apache.org") || !string.IsNullOrEmpty(repository.UserInfo))
            throw new InvalidDataException(L.T("Неизвестный Maven-репозиторий загрузчика"));
        var url = new Uri(repository, path).AbsoluteUri;
        var sha1 = library.TryGetProperty("sha1", out var hash) ? hash.GetString()! :
            (await http.GetStringAsync(url + ".sha1", ct).ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (sha1.Length != 40 || sha1.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException(L.T("Нет контрольной суммы библиотеки загрузчика"));
        long size;
        if (library.TryGetProperty("size", out var length)) size = length.GetInt64();
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            size = response.Content.Headers.ContentLength ?? throw new InvalidDataException(L.T("Репозиторий не сообщил размер библиотеки"));
        }
        return new DownloadFile(url, "game/libraries/" + path, sha1, size);
    }
}
