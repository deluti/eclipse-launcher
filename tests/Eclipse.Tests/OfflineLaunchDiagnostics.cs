using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using Eclipse;

internal static class OfflineLaunchDiagnostics
{
    internal static async Task<int> RunAsync(string dataDirectory, string checkDirectory, string version = "26.3", string? skinAgent = null)
    {
        var paths = new AppPaths(dataDirectory);
        var plan = JsonStore.Read<InstallPlan>(AppPaths.SafeChild(paths.Root, "plans/" + version + ".json")) ?? throw new InvalidOperationException("Prepare Minecraft first");
        var gameDirectory = Path.GetFullPath(Path.Combine(checkDirectory, "game"));
        Directory.CreateDirectory(gameDirectory);
        var fabric = version.StartsWith("fabric-loader-", StringComparison.Ordinal);
        if (fabric)
        {
            Directory.CreateDirectory(Path.Combine(gameDirectory, "mods"));
            using var archive = new ZipArchive(File.Create(Path.Combine(gameDirectory, "mods", "eclipse-probe.jar")), ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open());
            writer.Write("""{"schemaVersion":1,"id":"eclipse_probe","version":"1.0.0","name":"Eclipse probe","environment":"client"}""");
        }
        using var http = new HttpClient();
        var cosmeticPaths = new AppPaths(Path.Combine(checkDirectory, "cosmetic"));
        if (skinAgent != null)
        {
            var library = new SkinLibrary(cosmeticPaths); library.Select(library.Import(SkinProfileTests.Fixture(cosmeticPaths), false));
            Directory.CreateDirectory(cosmeticPaths.FilePath("support")); File.Copy(skinAgent, cosmeticPaths.FilePath("support/authlib-injector-1.2.8.jar"), true);
        }
        await using var skinRuntime = skinAgent == null ? null : await LocalSkinRuntime.StartAsync(http, cosmeticPaths, OfflineAccount.Create("Eclipse_Test"), new InlineProgress(), default);
        var info = await new Minecraft(http, paths).CreateOfflineStartInfoAsync(plan, OfflineAccount.Create("Eclipse_Test"), 4096, default,
            new Settings { WindowWidth = 854, WindowHeight = 480 });
        var gameDirIndex = info.ArgumentList.IndexOf("--gameDir");
        if (gameDirIndex < 0) throw new InvalidDataException("The game directory argument is missing");
        info.ArgumentList[gameDirIndex + 1] = gameDirectory;
        info.WorkingDirectory = gameDirectory;
        if (skinRuntime != null)
        {
            var arguments = await File.ReadAllLinesAsync(info.ArgumentList[0][1..]);
            var argFile = Path.Combine(checkDirectory, "skin-jvm.args");
            var custom = arguments.Take(arguments.Length - 1).Concat(skinRuntime.JvmArguments.Select(Minecraft.QuoteJavaArgument)).Append(arguments[^1]);
            await File.WriteAllLinesAsync(argFile, custom);
            info.ArgumentList[0] = "@" + argFile;
        }
        var lines = new ConcurrentQueue<string>();
        var guiReady = 0;
        var loaderReady = 0;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Java did not start");
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Enqueue(line);
                if (line.Contains("Fabric Loader", StringComparison.OrdinalIgnoreCase) || line.Contains("Quilt Loader", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref loaderReady, 1);
                if (line.Contains("Created:", StringComparison.Ordinal) && line.Contains("textures/atlas/gui", StringComparison.Ordinal))
                    Interlocked.Exchange(ref guiReady, 1);
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
            JsonStore.Write(Path.Combine(checkDirectory, "result.json"), new { guiLoaded = ready, loaderLoaded = Volatile.Read(ref loaderReady) == 1, skinAgentLoaded = lines.Any(line => line.Contains("Version: 1.2.8", StringComparison.Ordinal)), probeDetected = lines.Any(line => line.Contains("eclipse_probe", StringComparison.Ordinal)), nickname = "Eclipse_Test", version, process.ExitCode });
        }
        return ready ? 0 : 1;
    }
}
