namespace Eclipse;

internal sealed class GameProfiles(AppPaths paths)
{
    internal static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new InvalidOperationException(L.T("Название: от 1 до 64 символов."));
        return name;
    }
    internal ModdedProfile Rename(string id, string name)
    {
        ModLoaders.ValidateVersion(id);
        name = ValidateName(name);
        var all = ModdedProfile.ReadAll(paths);
        var existing = all.FirstOrDefault(p => p.Id == id) ?? new(id, id, "vanilla", "") { SharedDirectory = true };
        var renamed = existing with { Name = name };
        all.RemoveAll(p => p.Id == id); all.Insert(0, renamed);
        JsonStore.Write(paths.FilePath("profiles.json"), all);
        return renamed;
    }
    internal async Task<ModdedProfile> CopyAsync(string id, string name, bool copyContent, bool copyWorlds, IProgress<string> progress, CancellationToken ct)
    {
        ModLoaders.ValidateVersion(id); name = ValidateName(name);
        var all = ModdedProfile.ReadAll(paths);
        var source = all.FirstOrDefault(p => p.Id == id) ?? new(id, id, "vanilla", "") { SharedDirectory = true };
        if (name == source.ToString())
        {
            var basis = name;
            for (var number = 2; ; number++)
            {
                var suffix = " (" + number + ")";
                name = basis[..Math.Min(basis.Length, 64 - suffix.Length)] + suffix;
                if (!all.Any(p => p.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))) break;
            }
        }
        var runtime = ResolveRuntime(id, all);
        var newId = "instance-" + Guid.NewGuid().ToString("N");
        var profile = new ModdedProfile(newId, source.GameVersion, source.Loader, source.LoaderVersion) { Name = name, SourceVersion = runtime };
        var sourceDirectory = source.SharedDirectory ? paths.SharedGame : AppPaths.SafeChild(paths.Root, source.GameDirectory);
        var target = AppPaths.SafeChild(paths.Root, profile.GameDirectory);
        var staging = AppPaths.SafeChild(paths.Root, "instances/.creating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var roots = new List<string>();
            if (copyContent) roots.AddRange(["mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "options.txt", "servers.dat", "servers.dat_old"]);
            if (copyWorlds) roots.Add("saves");
            async Task CopyEntry(string from, string to)
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.T("Ссылки на внешние папки не копируются."));
                if (Directory.Exists(from))
                {
                    Directory.CreateDirectory(to);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(from)) await CopyEntry(entry, Path.Combine(to, Path.GetFileName(entry))).ConfigureAwait(false);
                }
                else
                {
                    progress.Report(L.T("Копирование: ") + Path.GetRelativePath(sourceDirectory, from));
                    await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }
            }
            foreach (var root in roots)
            {
                var from = Path.Combine(sourceDirectory, root);
                if (File.Exists(from) || Directory.Exists(from)) await CopyEntry(from, Path.Combine(staging, root)).ConfigureAwait(false);
            }
            Directory.CreateDirectory(Path.Combine(staging, "mods"));
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            all = ModdedProfile.ReadAll(paths);
            all.Insert(0, profile);
            JsonStore.Write(paths.FilePath("profiles.json"), all);
            return profile;
        }
        finally
        {
            // Only this operation's freshly generated staging directory can be removed.
            if (Directory.Exists(staging) && staging.StartsWith(Path.GetFullPath(paths.FilePath("instances")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(staging, true);
        }
    }
    internal static string ResolveRuntime(string id, IReadOnlyList<ModdedProfile> profiles)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            ModLoaders.ValidateVersion(id);
            if (!visited.Add(id)) throw new InvalidDataException(L.T("Обнаружен цикл игровых профилей."));
            var profile = profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null || string.IsNullOrEmpty(profile.SourceVersion)) return id;
            id = profile.SourceVersion;
        }
    }
    internal static InstallPlan InstancePlan(InstallPlan source, ModdedProfile profile) => new()
    {
        Version = profile.Id, BaseVersion = string.IsNullOrEmpty(source.BaseVersion) ? source.Version : source.BaseVersion,
        GameRelativeDirectory = profile.GameDirectory, JavaRelativePath = source.JavaRelativePath, JavaMajorVersion = source.JavaMajorVersion, MainClass = source.MainClass, AssetIndex = source.AssetIndex,
        ClassPath = [..source.ClassPath], NativeJars = [..source.NativeJars], NativeFiles = [..source.NativeFiles],
        JvmArguments = [..source.JvmArguments], GameArguments = [..source.GameArguments], Files = [..source.Files], RuntimeLinks = [..source.RuntimeLinks]
    };
}
