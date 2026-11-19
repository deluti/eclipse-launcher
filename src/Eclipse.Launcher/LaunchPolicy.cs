using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
        if (!GetPerformanceInfo(out var status, (uint)Marshal.SizeOf<NativeStatus>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        // System-wide commit headroom is required: the launcher is x86, but Java is x64.
        long Mb(nuint pages) => (long)((ulong)pages * (ulong)status.PageSize / 1048576);
        return new(Mb(status.PhysicalTotal), Mb(status.PhysicalAvailable), Mb(status.CommitLimit > status.CommitTotal ? status.CommitLimit - status.CommitTotal : 0));
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
        Span<byte> header = stackalloc byte[64]; file.ReadExactly(header);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (header[0] != 'M' || header[1] != 'Z' || offset < 64 || offset > file.Length - 6) throw new InvalidDataException(L.T("Повреждён исполняемый файл Java."));
        file.Position = offset; Span<byte> pe = stackalloc byte[6]; file.ReadExactly(pe);
        if (!pe[..4].SequenceEqual("PE\0\0"u8) || BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != 0x8664)
            throw new InvalidDataException(L.T("Для Minecraft нужна Java Windows x64."));
        return new(major, version, "x64", executable);
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
