using System.Runtime.InteropServices;

namespace Eclipse;

internal static class LinuxSelfTest
{
    internal static void Run()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Linux release requires x86_64 Linux.");

        var memory = MemoryStatus.Read();
        if (memory.TotalMb < 256 || memory.AvailableMb <= 0)
            throw new InvalidOperationException("Linux memory detection failed.");

        var root = Path.Combine(Path.GetTempPath(), "eclipse-linux-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var java = Path.Combine(root, "runtime", "java-runtime-epsilon", "bin", "java");
            Directory.CreateDirectory(Path.GetDirectoryName(java)!);
            File.WriteAllText(Path.Combine(root, "runtime", "java-runtime-epsilon", "release"), "JAVA_VERSION=\"25.0.1\"\n");
            var elf = new byte[64];
            elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
            elf[4] = 2; elf[5] = 1; elf[18] = 0x3e;
            File.WriteAllBytes(java, elf);
            File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var plan = new InstallPlan { JavaRelativePath = "runtime/java-runtime-epsilon/bin/java", JavaMajorVersion = 25 };
            var runtime = JavaRuntimeInfo.Inspect(paths, plan);
            if (runtime is null || runtime.Major != 25 || runtime.Architecture != "x64")
                throw new InvalidOperationException("Linux Java runtime validation failed.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
