using Avalonia;

namespace Eclipse;

internal static class Program
{
    internal static bool SmokeTest { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--core-smoke-test", StringComparer.Ordinal))
        {
            var paths = new AppPaths(LinuxPaths.DataRoot());
            LinuxSelfTest.Run();
            Console.WriteLine($"Eclipse {LauncherUpdates.CurrentVersion} · {paths.Root}");
            return 0;
        }
        SmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
