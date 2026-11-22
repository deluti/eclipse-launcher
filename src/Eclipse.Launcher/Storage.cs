using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Eclipse;

public sealed class AppPaths
{
    public string Root { get; }
    public string SharedGame => Path.Combine(Root, "game");
    public string Game => string.IsNullOrEmpty(activeGameDirectory) ? SharedGame : SafeChild(Root, activeGameDirectory);
    private string activeGameDirectory = "";
    internal void UseGameDirectory(string relative)
    {
        if (!string.IsNullOrEmpty(relative)) _ = SafeChild(Root, relative);
        activeGameDirectory = relative;
    }
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        var existed = Directory.Exists(Root);
        Directory.CreateDirectory(Root);
        if (OperatingSystem.IsLinux() && !existed)
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public string FilePath(string name) => Path.Combine(Root, name);
    public static string SafeChild(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Недопустимый путь в метаданных игры."));
        return full;
    }
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options);
    }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, Options));
        File.Move(temp, path, true);
    }
}

public sealed class Settings
{
    public string Language { get; set; } = "en";
    public string Version { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public bool AutomaticMemory { get; set; }
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public bool MinimizeOnLaunch { get; set; } = true;
    public bool InterfaceSounds { get; set; } = true;
    public bool OfflineMode { get; set; } = true;
    public string Nickname { get; set; } = "Player";
}

// Windows uses DPAPI. Unix keeps the credential file private to the current user
// (0600); no service, keyring helper or background process is required.
public static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = encrypt
                ? CryptProtectData(ref input, "Eclipse Minecraft session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
    public static void Save<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var temp = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temp, OperatingSystem.IsWindows() ? Transform(bytes, true) : bytes);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static T? Load<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var stored = File.ReadAllBytes(path);
        var bytes = OperatingSystem.IsWindows() ? Transform(stored, false) : stored;
        try { return JsonSerializer.Deserialize<T>(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
