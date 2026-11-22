using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Eclipse;

internal sealed record GraphicsAdapter(string Name, uint VendorId, ulong DedicatedBytes, ulong SharedBytes);
internal sealed record GraphicsReport(IReadOnlyList<GraphicsAdapter> Adapters, string JavaPreference)
{
    internal bool DedicatedAdapterAvailable => Adapters.Any(a => a.DedicatedBytes >= 512UL * 1024 * 1024);
}

internal static class WindowsGraphics
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint VendorId, DeviceId, SubsystemId, Revision;
        internal nuint DedicatedVideo, DedicatedSystem, SharedSystem;
        internal uint LuidLow; internal int LuidHigh; internal uint Flags;
    }
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumerateAdapter(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    internal static GraphicsReport Read(string? javaExecutable)
    {
        if (!OperatingSystem.IsWindows()) return ReadLinux();
        var adapters = new List<GraphicsAdapter>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(new Guid("770aae78-f26f-4dba-a829-253c83d1b387"), out factory) >= 0)
            {
                var enumerate = Method<EnumerateAdapter>(factory, 12); // IDXGIFactory1::EnumAdapters1
                for (uint i = 0; i < 16 && enumerate(factory, i, out var adapter) >= 0; i++)
                {
                    try
                    {
                        if (Method<GetDescription>(adapter, 10)(adapter, out var item) >= 0 && (item.Flags & 2) == 0)
                            adapters.Add(new(item.Description, item.VendorId, (ulong)item.DedicatedVideo, (ulong)item.SharedSystem));
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        var preference = "Windows default";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", false);
            if (javaExecutable != null && key?.GetValue(javaExecutable) is string value)
                preference = value.Contains("GpuPreference=2;", StringComparison.Ordinal) ? "High performance" : value.Contains("GpuPreference=1;", StringComparison.Ordinal) ? "Power saving" : "Windows default";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return new(adapters, preference);
    }
    private static GraphicsReport ReadLinux()
    {
        var adapters = new List<GraphicsAdapter>();
        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*", SearchOption.TopDirectoryOnly)
                         .Where(path => !Path.GetFileName(path).Contains('-', StringComparison.Ordinal)))
            {
                var device = Path.Combine(card, "device");
                if (!Directory.Exists(device)) continue;
                static uint Hex(string path)
                {
                    var value = File.Exists(path) ? File.ReadAllText(path).Trim() : "0";
                    return uint.TryParse(value.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                }
                static ulong Number(string path) => File.Exists(path) && ulong.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : 0;
                var vendor = Hex(Path.Combine(device, "vendor"));
                var id = Hex(Path.Combine(device, "device"));
                var name = $"{Path.GetFileName(card)} · {vendor:x4}:{id:x4}";
                adapters.Add(new(name, vendor, Number(Path.Combine(device, "mem_info_vram_total")), 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var prime = Environment.GetEnvironmentVariable("DRI_PRIME") ?? Environment.GetEnvironmentVariable("__NV_PRIME_RENDER_OFFLOAD");
        return new(adapters, string.IsNullOrEmpty(prime) ? "Linux default" : "PRIME offload: " + prime);
    }
}
