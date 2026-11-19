using System.Diagnostics;

namespace Eclipse;

internal sealed record ResourceSnapshot(long WorkingSetBytes, long PrivateBytes, long ManagedBytes, int Threads, double CpuMs)
{
    internal static ResourceSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false), process.Threads.Count, process.TotalProcessorTime.TotalMilliseconds);
    }
}
internal sealed record PreparationMeasurement(double Milliseconds, long HashedFiles, long DownloadedFiles, long ReceiptWrites);

// Only local technical measurements. No identity, tokens, command lines, user files or network export.
internal static class PerformanceJournal
{
    internal static void Write(AppPaths paths, string name, object value)
    {
        try { JsonStore.Write(paths.FilePath("diagnostics/" + name + ".json"), value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Debug.WriteLine(ex.Message); }
    }
    internal static void Startup(AppPaths paths, double firstPaintMs, double readyMs)
        => Write(paths, "startup", new { launcherVersion = LauncherUpdates.CurrentVersion.ToString(3), firstPaintMs, readyMs, resources = ResourceSnapshot.Capture() });
}

internal sealed class LaunchMeasurement(AppPaths paths, string version, PreparationMeasurement? preparation, long requestedAt)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly object gate = new();
    private double? firstOutputMs, guiReadyMs, processStartedMs;
    private object? resourcesDuringGame;
    internal JavaRuntimeInfo? Java;
    internal MemoryBudget? Memory;
    internal void ProcessStarted()
    {
        processStartedMs = requestedAt == 0 ? clock.Elapsed.TotalMilliseconds : Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds;
        Save("running", null);
    }
    internal void Output(string line)
    {
        lock (gate)
        {
            firstOutputMs ??= clock.Elapsed.TotalMilliseconds;
            if (guiReadyMs == null && line.Contains("Created:", StringComparison.Ordinal) && line.Contains("textures/atlas/gui", StringComparison.Ordinal))
            { guiReadyMs = clock.Elapsed.TotalMilliseconds; Save("gui-ready", null); }
        }
    }
    internal async Task SampleOnceAsync(Process game, CancellationToken ct)
    {
        try
        {
            var before = ResourceSnapshot.Capture(); var started = Stopwatch.GetTimestamp();
            await Task.Delay(3000, ct).ConfigureAwait(false);
            var after = ResourceSnapshot.Capture(); game.Refresh();
            if (game.HasExited) return;
            resourcesDuringGame = new { launcher = after, launcherCpuMsPerSecond = (after.CpuMs - before.CpuMs) / Stopwatch.GetElapsedTime(started).TotalSeconds,
                gameWorkingSetBytes = game.WorkingSet64, gameCpuMs = game.TotalProcessorTime.TotalMilliseconds,
                graphics = WindowsGraphics.Read(Java?.Executable) };
            lock (gate) Save("running", null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    internal void Complete(int? exitCode)
    {
        lock (gate) Save(exitCode == null ? "failed" : "exited", exitCode);
    }
    private void Save(string state, int? exitCode)
        => PerformanceJournal.Write(paths, "launch", new { launcherVersion = LauncherUpdates.CurrentVersion.ToString(3), version, state, exitCode,
            processStartedMs, firstOutputMs, guiReadyMs, elapsedMs = clock.Elapsed.TotalMilliseconds, preparation, java = Java, memory = Memory,
            garbageCollector = "HotSpot defaults (no tuning overrides)", resourcesDuringGame });
}
