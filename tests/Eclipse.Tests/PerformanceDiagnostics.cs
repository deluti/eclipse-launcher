using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Eclipse;

internal static class PerformanceDiagnostics
{
    internal static async Task<int> RunAsync(string gameRoot, string resultFile)
    {
        var paths = new AppPaths(gameRoot);
        using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Warm benchmark must use no network")));
        var preparation = new List<double>(); var receiptWrites = new List<bool>();
        var minecraft = new Minecraft(http, paths);
        for (var run = 0; run < 6; run++)
        {
            var receipt = paths.FilePath("verified-files.json");
            var before = File.GetLastWriteTimeUtc(receipt);
            var clock = Stopwatch.StartNew();
            await minecraft.PrepareAsync("26.3", new InlineProgress(), default);
            preparation.Add(clock.Elapsed.TotalMilliseconds);
            receiptWrites.Add(before != File.GetLastWriteTimeUtc(receipt));
        }
        var afterGamePreparation = new List<double>();
        var releaseCaches = typeof(Minecraft).GetMethod("ReleasePreparationCache", BindingFlags.NonPublic | BindingFlags.Instance);
        for (var run = 0; run < 4; run++)
        {
            releaseCaches?.Invoke(minecraft, null);
            var clock = Stopwatch.StartNew(); await minecraft.PrepareAsync("26.3", new InlineProgress(), default);
            afterGamePreparation.Add(clock.Elapsed.TotalMilliseconds);
        }
        object? ui = null; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                var fixture = new AppPaths(Path.Combine(Path.GetDirectoryName(resultFile)!, "benchmark-ui-" + Guid.NewGuid().ToString("N")));
                JsonStore.Write(fixture.FilePath("versions.json"), new { latest = new { release = "26.3" }, versions = new[] { new { id = "26.3", type = "release" } } });
                JsonStore.Write(fixture.FilePath("settings.json"), new Settings { MinimizeOnLaunch = false });
                using var updates = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
                var start = Stopwatch.StartNew();
                using var form = new MainForm(fixture, false, new LauncherConfig(), new LauncherUpdates(updates));
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        form.Refresh(); var firstPaintMs = start.Elapsed.TotalMilliseconds;
                        while (!form.Ready) await Task.Delay(10);
                        var readyMs = start.Elapsed.TotalMilliseconds;
                        using var process = Process.GetCurrentProcess();
                        async Task<object> Sample()
                        {
                            process.Refresh(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
                            var frames = form.Navigation.PaintCount;
                            await Task.Delay(2500); process.Refresh();
                            return new { cpuMsPerSecond = (process.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalSeconds,
                                framesPerSecond = (form.Navigation.PaintCount - frames) / clock.Elapsed.TotalSeconds,
                                workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, managedBytes = GC.GetTotalMemory(false), threads = process.Threads.Count };
                        }
                        await Task.Delay(300); var idle = await Sample();
                        // Identical event path on old and new builds, with no game/settings modifications.
                        var service = (Minecraft)typeof(MainForm).GetField("minecraft", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                        ((Action?)typeof(Minecraft).GetField("GameStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service))?.Invoke();
                        await Task.Delay(500); var gameVisible = await Sample();
                        form.WindowState = FormWindowState.Minimized; await Task.Delay(200); var gameMinimized = await Sample();
                        ui = new { firstPaintMs, readyMs, idle, gameVisible, gameMinimized };
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { typeof(MainForm).GetField("running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false); form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Benchmark UI timed out");
        if (failure != null) throw failure;
        var result = new { version = LauncherUpdates.CurrentVersion.ToString(3), preparationMs = preparation, afterGamePreparationMs = afterGamePreparation, receiptWrites, ui };
        JsonStore.Write(resultFile, result); Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
    }
}
