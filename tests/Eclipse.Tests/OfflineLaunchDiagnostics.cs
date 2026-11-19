using System.Collections.Concurrent;
using System.Diagnostics;
using Eclipse;

internal static class OfflineLaunchDiagnostics
{
    internal static async Task<int> RunAsync(string dataDirectory, string checkDirectory, string version = "26.3")
    {
        var paths = new AppPaths(dataDirectory);
        var plan = JsonStore.Read<InstallPlan>(AppPaths.SafeChild(paths.Root, "plans/" + version + ".json")) ?? throw new InvalidOperationException("Prepare Minecraft first");
        var gameDirectory = Path.GetFullPath(Path.Combine(checkDirectory, "game"));
        Directory.CreateDirectory(gameDirectory);
        var fabric = version.StartsWith("fabric-loader-", StringComparison.Ordinal);
        using var http = new HttpClient();
        typeof(Minecraft).Assembly.GetType("Eclipse.JavaRuntimeInfo")?.GetMethod("Inspect", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, [paths, plan]);
        var info = await new Minecraft(http, paths).CreateOfflineStartInfoAsync(plan, OfflineAccount.Create("Eclipse_Test"), 4096, default,
            new Settings { WindowWidth = 854, WindowHeight = 480 });
        var gameDirIndex = info.ArgumentList.IndexOf("--gameDir");
        if (gameDirIndex < 0) throw new InvalidDataException("The game directory argument is missing");
        info.ArgumentList[gameDirIndex + 1] = gameDirectory;
        info.WorkingDirectory = gameDirectory;
        var lines = new ConcurrentQueue<string>();
        var guiReady = 0;
        var loaderReady = 0;
        var launchWatch = Stopwatch.StartNew();
        double? guiReadyMs = null;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Java did not start");
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Enqueue(line);
                if (line.Contains("Fabric Loader", StringComparison.OrdinalIgnoreCase) || line.Contains("Quilt Loader", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref loaderReady, 1);
                if (line.Contains("Created:", StringComparison.Ordinal) && line.Contains("textures/atlas/gui", StringComparison.Ordinal))
                { guiReadyMs ??= launchWatch.Elapsed.TotalMilliseconds; Interlocked.Exchange(ref guiReady, 1); }
            }
        }
        var output = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        var ready = false;
        try
        {
            var timer = Stopwatch.StartNew();
            while (!process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(40) && Volatile.Read(ref guiReady) == 0) await Task.Delay(100);
            if (!process.HasExited && Volatile.Read(ref guiReady) == 1) { await Task.Delay(1000); ready = !process.HasExited && (!fabric || Volatile.Read(ref loaderReady) == 1); }
            Console.WriteLine(ready ? "PASS real local launch: Minecraft loaded GUI textures as Eclipse_Test without launcher Microsoft sign-in" : "FAIL local launch: inspect java.log");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Refresh(); process.CloseMainWindow();
                var exited = process.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(3000)) != exited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await output;
            Directory.CreateDirectory(checkDirectory);
            await File.WriteAllLinesAsync(Path.Combine(checkDirectory, "java.log"), lines);
            JsonStore.Write(Path.Combine(checkDirectory, "result.json"), new { guiLoaded = ready, guiReadyMs, loaderLoaded = Volatile.Read(ref loaderReady) == 1,
                noAgents = !File.ReadAllText(info.ArgumentList[0][1..]).Contains("-javaagent", StringComparison.OrdinalIgnoreCase), version, process.ExitCode });
        }
        return ready ? 0 : 1;
    }
}
