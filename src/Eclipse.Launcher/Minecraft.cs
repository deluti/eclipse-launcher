using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eclipse;

public sealed class InstallPlan
{
    public int Format { get; set; } = 1;
    public string Version { get; set; } = "";
    public string BaseVersion { get; set; } = "";
    public string GameRelativeDirectory { get; set; } = "";
    public string JavaRelativePath { get; set; } = "";
    public string MainClass { get; set; } = "";
    public string AssetIndex { get; set; } = "";
    public List<string> ClassPath { get; set; } = [];
    public List<string> NativeJars { get; set; } = [];
    public List<string> NativeFiles { get; set; } = [];
    public List<string> JvmArguments { get; set; } = [];
    public List<string> GameArguments { get; set; } = [];
    public List<DownloadFile> Files { get; set; } = [];
}

public sealed class Minecraft(HttpClient http, AppPaths paths)
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string RuntimeUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    public async Task<JsonElement> VersionsAsync(bool refresh, CancellationToken ct)
    {
        var file = paths.FilePath("versions.json");
        if (!refresh && File.Exists(file))
            return JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
        var manifest = await Network.JsonAsync(http, HttpMethod.Get, ManifestUrl, null, null, ct).ConfigureAwait(false);
        JsonStore.Write(file, manifest);
        return manifest;
    }

    public async Task<InstallPlan> PrepareAsync(string version, IProgress<string> progress, CancellationToken ct)
    {
        // A 32-bit launcher can start the separate 64-bit Java process on 64-bit Windows.
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Для запуска Minecraft требуется 64-битная Windows (x64).");
        if (!Regex.IsMatch(version, "\\A[A-Za-z0-9_.+-]+\\z")) throw new InvalidDataException("Недопустимая версия.");
        var planPath = AppPaths.SafeChild(paths.Root, $"plans/{version}.json");
        var downloads = new Downloads(http, paths);
        InstallPlan? plan = null;
        try { plan = JsonStore.Read<InstallPlan>(planPath); } catch (JsonException) { }
        if (plan is { Format: 1 } && plan.Version == version)
        {
            await downloads.EnsureAllAsync(plan.Files, progress, ct).ConfigureAwait(false);
            if (plan.NativeFiles.Any(f => !File.Exists(AppPaths.SafeChild(paths.Root, f)))) ExtractNatives(plan);
            return plan;
        }
        if (ModdedProfile.ReadAll(paths).FirstOrDefault(p => p.Id == version) is { } profile)
            return await new ModLoaders(http, paths, this).InstallAsync(profile.Loader, profile.GameVersion, profile.LoaderVersion, progress, ct).ConfigureAwait(false);

        progress.Report("Получение описания Minecraft " + version + "…");
        var manifest = await VersionsAsync(false, ct).ConfigureAwait(false);
        var entry = manifest.GetProperty("versions").EnumerateArray().FirstOrDefault(v => v.Text("id") == version);
        if (entry.ValueKind == JsonValueKind.Undefined)
        {
            manifest = await VersionsAsync(true, ct).ConfigureAwait(false);
            entry = manifest.GetProperty("versions").EnumerateArray().First(v => v.Text("id") == version);
        }
        // Manifest entry includes a SHA-1 but no size. Obtain metadata bytes and verify before parsing.
        var metadata = await ReadVerifiedJsonAsync(entry.Text("url"), entry.Text("sha1"), ct).ConfigureAwait(false);
        if (!metadata.TryGetProperty("arguments", out var arguments) || !metadata.TryGetProperty("javaVersion", out var javaVersion)
            || javaVersion.GetProperty("majorVersion").GetInt32() < 17)
            throw new NotSupportedException("Сейчас поддерживаются версии Minecraft 1.18 и новее.");
        plan = new InstallPlan
        {
            Version = version, MainClass = metadata.Text("mainClass"),
            AssetIndex = metadata.GetProperty("assetIndex").Text("id"),
            JvmArguments = SelectArguments(arguments.GetProperty("jvm")),
            GameArguments = SelectArguments(arguments.GetProperty("game"))
        };
        var clientPath = $"game/versions/{version}/{version}.jar";
        plan.Files.Add(FromMetadata(metadata.GetProperty("downloads").GetProperty("client"), clientPath));
        foreach (var library in metadata.GetProperty("libraries").EnumerateArray())
        {
            if (!Allowed(library)) continue;
            var libDownloads = library.GetProperty("downloads");
            if (libDownloads.TryGetProperty("artifact", out var artifact))
            {
                var relative = "game/libraries/" + artifact.Text("path");
                plan.Files.Add(FromMetadata(artifact, relative));
                plan.ClassPath.Add(relative);
            }
            if (library.TryGetProperty("natives", out var natives) && natives.TryGetProperty("windows", out var classifier))
            {
                var key = classifier.GetString()!.Replace("${arch}", "64");
                var native = libDownloads.GetProperty("classifiers").GetProperty(key);
                var relative = "game/libraries/" + native.Text("path");
                plan.Files.Add(FromMetadata(native, relative));
                plan.NativeJars.Add(relative);
            }
        }
        plan.ClassPath.Add(clientPath);
        var indexInfo = metadata.GetProperty("assetIndex");
        var indexPath = $"game/assets/indexes/{plan.AssetIndex}.json";
        var indexFile = FromMetadata(indexInfo, indexPath);
        await downloads.EnsureAsync(indexFile, ct).ConfigureAwait(false);
        plan.Files.Add(indexFile);
        var index = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(AppPaths.SafeChild(paths.Root, indexPath), ct).ConfigureAwait(false));
        if ((index.TryGetProperty("virtual", out var virtualFlag) && virtualFlag.GetBoolean())
            || (index.TryGetProperty("map_to_resources", out var map) && map.GetBoolean()))
            throw new NotSupportedException("Старый формат ресурсов пока не поддерживается.");
        foreach (var asset in index.GetProperty("objects").EnumerateObject())
        {
            var hash = asset.Value.Text("hash");
            plan.Files.Add(new($"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                $"game/assets/objects/{hash[..2]}/{hash}", hash, asset.Value.GetProperty("size").GetInt64()));
        }

        progress.Report("Подготовка официальной Java…");
        var component = javaVersion.Text("component");
        var runtimes = await Network.JsonAsync(http, HttpMethod.Get, RuntimeUrl, null, null, ct).ConfigureAwait(false);
        var runtime = runtimes.GetProperty("windows-x64").GetProperty(component).EnumerateArray().FirstOrDefault();
        if (runtime.ValueKind == JsonValueKind.Undefined) throw new NotSupportedException("Mojang не опубликовала нужную Java для Windows x64.");
        var runtimeManifest = runtime.GetProperty("manifest");
        var runtimeJson = await ReadVerifiedJsonAsync(runtimeManifest.Text("url"), runtimeManifest.Text("sha1"), ct).ConfigureAwait(false);
        var runtimeRoot = $"runtime/{component}";
        plan.JavaRelativePath = runtimeRoot + "/bin/javaw.exe";
        foreach (var file in runtimeJson.GetProperty("files").EnumerateObject())
        {
            switch (file.Value.Text("type"))
            {
                case "file":
                    plan.Files.Add(FromMetadata(file.Value.GetProperty("downloads").GetProperty("raw"), runtimeRoot + "/" + file.Name));
                    break;
                case "directory": break;
                default: throw new NotSupportedException("Неизвестный тип файла в описании Java. Установка остановлена.");
            }
        }
        if (metadata.TryGetProperty("logging", out var logging) && logging.TryGetProperty("client", out var log))
        {
            var logFile = log.GetProperty("file");
            var relative = "game/assets/log_configs/" + logFile.Text("id");
            plan.Files.Add(FromMetadata(logFile, relative));
            plan.JvmArguments.Add(log.Text("argument").Replace("${path}", AppPaths.SafeChild(paths.Root, relative)));
        }
        await downloads.EnsureAllAsync(plan.Files, progress, ct).ConfigureAwait(false);
        ExtractNatives(plan);
        JsonStore.Write(planPath, plan);
        return plan;
    }

    private async Task<JsonElement> ReadVerifiedJsonAsync(string url, string sha1, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host is not ("piston-meta.mojang.com" or "launchermeta.mojang.com"))
            throw new InvalidDataException("Неизвестный источник метаданных Mojang.");
        var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        if (!Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).Equals(sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма описания Mojang не совпала.");
        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }

    private static DownloadFile FromMetadata(JsonElement data, string relative) =>
        new(data.Text("url"), relative, data.Text("sha1"), data.GetProperty("size").GetInt64());

    internal static bool Allowed(JsonElement value)
    {
        if (!value.TryGetProperty("rules", out var rules)) return true;
        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name) && name.GetString() != "windows") matches = false;
                if (os.TryGetProperty("arch", out var arch) && arch.GetString() != "x86_64" && arch.GetString() != "amd64") matches = false;
                if (os.TryGetProperty("version", out var version) && !Regex.IsMatch(Environment.OSVersion.Version.ToString(), version.GetString()!)) matches = false;
                if (os.TryGetProperty("versionRange", out var range))
                {
                    var current = Environment.OSVersion.Version;
                    if (range.TryGetProperty("min", out var min) && current < Version.Parse(min.GetString()!)) matches = false;
                    if (range.TryGetProperty("max", out var max) && current >= Version.Parse(max.GetString()!)) matches = false;
                }
            }
            if (rule.TryGetProperty("features", out var features))
                foreach (var feature in features.EnumerateObject())
                    if (feature.Value.ValueKind != JsonValueKind.False) matches = false;
            if (matches) allowed = rule.Text("action") == "allow";
        }
        return allowed;
    }

    internal static List<string> SelectArguments(JsonElement arguments)
    {
        var result = new List<string>();
        foreach (var argument in arguments.EnumerateArray())
        {
            if (argument.ValueKind == JsonValueKind.String) { result.Add(argument.GetString()!); continue; }
            if (!Allowed(argument)) continue;
            var value = argument.GetProperty("value");
            if (value.ValueKind == JsonValueKind.String) result.Add(value.GetString()!);
            else result.AddRange(value.EnumerateArray().Select(v => v.GetString()!));
        }
        return result;
    }

    private void ExtractNatives(InstallPlan plan)
    {
        plan.NativeFiles.Clear();
        foreach (var relative in plan.NativeJars)
        {
            using var archive = ZipFile.OpenRead(AppPaths.SafeChild(paths.Root, relative));
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var targetRelative = $"game/versions/{(string.IsNullOrEmpty(plan.BaseVersion) ? plan.Version : plan.BaseVersion)}/natives/" + entry.FullName;
                var target = AppPaths.SafeChild(paths.Root, targetRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
                plan.NativeFiles.Add(targetRelative);
            }
        }
    }

    public async Task<int> LaunchAsync(InstallPlan plan, LicensedAccount account, int memoryMb, IProgress<string> progress, CancellationToken ct, Settings? options = null)
    {
        // Revalidate after a potentially long first installation. Never accept a cached ownership flag.
        if (DateTimeOffset.UtcNow - account.VerifiedAt > TimeSpan.FromSeconds(60))
        {
            progress.Report("Повторная проверка лицензии перед запуском…");
            account = await LicensedAccount.VerifyAsync(http, account.AccessToken, ct, account.ClientId, account.Xuid).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return await RunGameAsync(plan, OnlineProfile(account), memoryMb, progress, ct, options).ConfigureAwait(false);
    }

    public Task<int> LaunchOfflineAsync(InstallPlan plan, OfflineAccount account, int memoryMb, IProgress<string> progress, CancellationToken ct, Settings? options = null)
        => RunGameAsync(plan, OfflineProfile(account), memoryMb, progress, ct, options);

    private sealed record LaunchProfile(string Name, string Uuid, string AccessToken, string ClientId, string Xuid, string UserType);
    private static LaunchProfile OnlineProfile(LicensedAccount account) => new(account.Name, account.Uuid, account.AccessToken, account.ClientId, account.Xuid, "msa");
    private static LaunchProfile OfflineProfile(OfflineAccount account) => new(account.Name, account.Uuid, "0", "", "", "legacy");

    private async Task<int> RunGameAsync(InstallPlan plan, LaunchProfile account, int memoryMb, IProgress<string> progress, CancellationToken ct, Settings? options)
    {
        ct.ThrowIfCancellationRequested();
        var info = await CreateStartInfoAsync(plan, account, memoryMb, ct, options).ConfigureAwait(false);
        Directory.CreateDirectory(paths.FilePath("logs"));
        await using var log = new StreamWriter(paths.FilePath("logs/game-latest.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        progress.Report("Запуск Minecraft…");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить Java.");
        progress.Report("Minecraft запущен. PID " + process.Id);
        using var gate = new SemaphoreSlim(1);
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var safeLine = account.UserType == "msa" ? line.Replace(account.AccessToken, "[REDACTED]", StringComparison.Ordinal) : line;
                    await log.WriteLineAsync(safeLine).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
        }
        await Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError), process.WaitForExitAsync()).ConfigureAwait(false);
        return process.ExitCode;
    }

    internal Task<ProcessStartInfo> CreateStartInfoAsync(InstallPlan plan, LicensedAccount account, int memoryMb, CancellationToken ct, Settings? options = null)
        => CreateStartInfoAsync(plan, OnlineProfile(account), memoryMb, ct, options);

    internal Task<ProcessStartInfo> CreateOfflineStartInfoAsync(InstallPlan plan, OfflineAccount account, int memoryMb, CancellationToken ct, Settings? options = null)
        => CreateStartInfoAsync(plan, OfflineProfile(account), memoryMb, ct, options);

    private async Task<ProcessStartInfo> CreateStartInfoAsync(InstallPlan plan, LaunchProfile account, int memoryMb, CancellationToken ct, Settings? options)
    {
        var nativeDir = AppPaths.SafeChild(paths.Root, $"game/versions/{(string.IsNullOrEmpty(plan.BaseVersion) ? plan.Version : plan.BaseVersion)}/natives");
        Directory.CreateDirectory(nativeDir);
        var gameDirectory = string.IsNullOrEmpty(plan.GameRelativeDirectory) ? paths.SharedGame : AppPaths.SafeChild(paths.Root, plan.GameRelativeDirectory);
        Directory.CreateDirectory(gameDirectory);
        var variables = new Dictionary<string, string>
        {
            ["natives_directory"] = nativeDir, ["launcher_name"] = "Eclipse", ["launcher_version"] = "0.1",
            ["classpath"] = string.Join(Path.PathSeparator, plan.ClassPath.Select(p => AppPaths.SafeChild(paths.Root, p))),
            ["classpath_separator"] = Path.PathSeparator.ToString(), ["library_directory"] = Path.Combine(paths.SharedGame, "libraries"),
            ["auth_player_name"] = account.Name, ["version_name"] = plan.Version, ["game_directory"] = gameDirectory,
            ["assets_root"] = Path.Combine(paths.SharedGame, "assets"), ["assets_index_name"] = plan.AssetIndex,
            ["auth_uuid"] = account.Uuid, ["auth_access_token"] = account.AccessToken, ["clientid"] = account.ClientId,
            ["auth_xuid"] = account.Xuid, ["user_type"] = account.UserType, ["version_type"] = "release", ["user_properties"] = "{}"
        };
        string Expand(string value) => Regex.Replace(value, @"\$\{([^}]+)\}", match => variables.TryGetValue(match.Groups[1].Value, out var replacement)
            ? replacement : throw new NotSupportedException("Неизвестный аргумент Minecraft: " + match.Groups[1].Value));
        var jvm = plan.JvmArguments.Select(Expand).ToList();
        jvm.Add("-Xms256M");
        jvm.Add("-Xmx" + Math.Clamp(memoryMb, 1024, 32768) + "M");
        jvm.Add(plan.MainClass);
        // JVM argument file avoids Windows' command line limit; it contains no account credentials.
        var argFile = AppPaths.SafeChild(paths.Root, $"game/versions/{plan.Version}/jvm.args");
        await File.WriteAllLinesAsync(argFile, jvm.Select(QuoteJavaArgument), new UTF8Encoding(false), ct).ConfigureAwait(false);
        var info = new ProcessStartInfo(AppPaths.SafeChild(paths.Root, plan.JavaRelativePath))
        {
            WorkingDirectory = gameDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("@" + argFile);
        foreach (var arg in plan.GameArguments.Select(Expand)) info.ArgumentList.Add(arg);
        if (options != null)
        {
            info.ArgumentList.Add("--width"); info.ArgumentList.Add(Math.Clamp(options.WindowWidth, 640, 7680).ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--height"); info.ArgumentList.Add(Math.Clamp(options.WindowHeight, 360, 4320).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (options.Fullscreen) info.ArgumentList.Add("--fullscreen");
        }
        // Avoid externally injected JVM flags and Java agents.
        foreach (var key in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH" }) info.Environment.Remove(key);
        return info;
    }

    internal static string QuoteJavaArgument(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
