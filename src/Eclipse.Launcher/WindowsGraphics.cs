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
}
