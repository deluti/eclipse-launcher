using System.Diagnostics;

namespace Eclipse;

internal static class Program
{
    internal static readonly Stopwatch Startup = Stopwatch.StartNew();
    static Program() { } // Prevent beforefieldinit from deferring the timer until the first paint.
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(AppContext.BaseDirectory, "data");
        var index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) root = args[index + 1];
        try
        {
            var paths = new AppPaths(root);
            using var instance = new Mutex(true, "Local\\EclipseLauncher-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..20], out var first);
            if (!first) { MessageBox.Show("Eclipse уже открыт для этой папки данных.", "Eclipse"); return; }
            using var window = new MainForm(paths, args.Contains("--smoke-test"));
            if (!args.Contains("--smoke-test")) window.Shown += (_, _) => UiSounds.Start();
            try { Application.Run(window); }
            finally { UiSounds.Stop(); }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть Eclipse: " + ex.Message, "Eclipse", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
