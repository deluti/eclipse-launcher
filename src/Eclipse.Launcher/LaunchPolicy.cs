using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Eclipse;

internal readonly record struct MemoryStatus(long TotalMb, long AvailableMb, long CommitAvailableMb)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        internal uint Length;
        internal nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        internal uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out NativeStatus status, uint size);
    internal static MemoryStatus Read()
    {
        if (!OperatingSystem.IsWindows()) return ReadProcMemInfo();
        if (!GetPerformanceInfo(out var status, (uint)Marshal.SizeOf<NativeStatus>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        // System-wide commit headroom is required: the launcher is x86, but Java is x64.
        long Mb(nuint pages) => (long)((ulong)pages * (ulong)status.PageSize / 1048576);
        return new(Mb(status.PhysicalTotal), Mb(status.PhysicalAvailable), Mb(status.CommitLimit > status.CommitTotal ? status.CommitLimit - status.CommitTotal : 0));
    }
    private static MemoryStatus ReadProcMemInfo()
    {
        try
        {
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                var number = line[(separator + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (long.TryParse(number, out var kb)) values[line[..separator]] = kb;
            }
            var total = values.GetValueOrDefault("MemTotal") / 1024;
            var available = values.GetValueOrDefault("MemAvailable", values.GetValueOrDefault("MemFree")) / 1024;
            var commit = Math.Max(0, values.GetValueOrDefault("CommitLimit") - values.GetValueOrDefault("Committed_AS")) / 1024;
            if (total > 0 && available > 0) return new(total, available, commit > 0 ? commit : available);
        }
        catch (IOException) { }
        var fallback = Math.Max(1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576);
        return new(fallback, Math.Max(1024, fallback / 2), Math.Max(1024, fallback / 2));
    }
}

internal sealed record MemoryBudget(int MaximumMb, int InitialMb, bool Automatic, long TotalMb, long AvailableMb, int ModCount)
{
    internal static MemoryBudget Calculate(MemoryStatus memory, bool automatic, int requestedMb, int javaMajor, int modCount)
    {
        // Reserve physical RAM for Windows, native game allocations, graphics and other applications.
        var reserve = Math.Max(1536, memory.TotalMb / 8);
        var capacity = Math.Max(1024, Math.Min(memory.TotalMb * 2 / 3, Math.Min(memory.AvailableMb - reserve, memory.CommitAvailableMb - 1024)));
        var desired = modCount > 150 ? 8192 : modCount > 50 ? 6144 : modCount > 0 ? 4096 : javaMajor >= 21 ? 3072 : 2048;
        var maximum = automatic ? (int)(Math.Min(desired, capacity) / 512 * 512) : Math.Clamp(requestedMb, 1024, 32768);
        return new(maximum, 256, automatic, memory.TotalMb, memory.AvailableMb, modCount);
    }
    internal static int CountMods(string gameDirectory)
    {
        var mods = Path.Combine(gameDirectory, "mods");
        try { return Directory.Exists(mods) ? Directory.EnumerateFiles(mods, "*.jar", SearchOption.TopDirectoryOnly).Take(201).Count() : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

internal sealed record JavaRuntimeInfo(int Major, string Version, string Architecture, string Executable)
{
    internal static int RequiredMajor(InstallPlan plan)
    {
        var component = plan.JavaRelativePath.Replace('\\', '/').Split('/').Skip(1).FirstOrDefault() ?? "";
        return plan.JavaMajorVersion > 0 ? plan.JavaMajorVersion : component switch
        { "java-runtime-gamma" => 17, "java-runtime-delta" => 21, "java-runtime-epsilon" => 25, _ => 0 };
    }
    internal static JavaRuntimeInfo? Inspect(AppPaths paths, InstallPlan plan)
    {
        var executable = AppPaths.SafeChild(paths.Root, plan.JavaRelativePath);
        // Old plans are upgraded in memory; the runtime remains the one selected by Mojang's metadata.
        var expected = RequiredMajor(plan);
        if (expected == 0) return null; // Non-Java process fixtures / legacy plans without a known runtime family.
        var releaseFile = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(executable))!, "release");
        var release = File.ReadAllText(releaseFile);
        var version = Regex.Match(release, "(?m)^JAVA_VERSION=\"([^\"]+)\"").Groups[1].Value;
        if (!int.TryParse(version.Split('.', '-', '+')[0], out var major) || major != expected)
            throw new InvalidDataException(L.F("Нужна Java {0}; установленная Java несовместима. Повторите подготовку версии.", expected));
        using var file = File.OpenRead(executable);
        if (OperatingSystem.IsWindows()) ValidateWindowsExecutable(file);
        else if (OperatingSystem.IsLinux()) ValidateLinuxExecutable(file, executable);
        else throw new PlatformNotSupportedException();
        return new(major, version, "x64", executable);
    }
    private static void ValidateWindowsExecutable(Stream file)
    {
        Span<byte> header = stackalloc byte[64]; file.ReadExactly(header);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (header[0] != 'M' || header[1] != 'Z' || offset < 64 || offset > file.Length - 6) throw new InvalidDataException(L.T("Повреждён исполняемый файл Java."));
        file.Position = offset; Span<byte> pe = stackalloc byte[6]; file.ReadExactly(pe);
        if (!pe[..4].SequenceEqual("PE\0\0"u8) || BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != 0x8664)
            throw new InvalidDataException(L.T("Для Minecraft нужна Java Windows x64."));
    }
    [SupportedOSPlatform("linux")]
    private static void ValidateLinuxExecutable(Stream file, string executable)
    {
        Span<byte> elf = stackalloc byte[20]; file.ReadExactly(elf);
        if (!elf[..4].SequenceEqual("\u007fELF"u8) || elf[4] != 2 || elf[5] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(elf[18..]) != 0x3e)
            throw new InvalidDataException(L.T("Для Minecraft нужна Java Linux x64."));
        var mode = File.GetUnixFileMode(executable);
        if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new InvalidDataException(L.T("Исполняемый файл Java не имеет права запуска."));
    }
    internal static void ValidateArguments(IEnumerable<string> arguments)
    {
        // Profiles can provide ordinary official JVM flags; agents and native injection are never loaded.
        foreach (var argument in arguments)
            if (argument.StartsWith("-javaagent", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-agentlib", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-agentpath", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("-Xrun", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("@", StringComparison.Ordinal))
                throw new InvalidDataException(L.T("Профиль содержит запрещённый агент или вложенный файл JVM."));
    }
}
