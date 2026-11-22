using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eclipse;

internal static class OptimizationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static AppPaths Paths(string root) => new(Path.Combine(root, "optimization", Guid.NewGuid().ToString("N")));
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    internal static Task MemoryAsync(string root)
    {
        var memory = new MemoryStatus(16384, 10000, 12000);
        Check(MemoryBudget.Calculate(memory, true, 32768, 21, 0).MaximumMb == 3072, "Vanilla avoids oversized heaps");
        Check(MemoryBudget.Calculate(memory, true, 1024, 17, 100).MaximumMb == 6144, "Mod count increases the local recommendation");
        Check(MemoryBudget.Calculate(new(8192, 3500, 5000), true, 8192, 25, 200).MaximumMb == 1536, "Available RAM constrains automatic heaps");
        Check(MemoryBudget.Calculate(memory, false, 7168, 25, 200).MaximumMb == 7168, "Manual heap is preserved");
        var paths = Paths(root); File.WriteAllText(paths.FilePath("settings.json"), "{\"MemoryMb\":6144}");
        var old = JsonStore.Read<Settings>(paths.FilePath("settings.json"))!;
        Check(old.MemoryMb == 6144 && !old.AutomaticMemory, "Existing manual settings do not silently become automatic");
        old.AutomaticMemory = true; JsonStore.Write(paths.FilePath("settings.json"), old);
        Check(JsonStore.Read<Settings>(paths.FilePath("settings.json")) is { MemoryMb: 6144, AutomaticMemory: true }, "Both mode and manual value persist");
        var live = MemoryStatus.Read(); Check(live.TotalMb > 0 && live.AvailableMb > 0, "Native RAM check works without another process");
        return Task.CompletedTask;
    }
    internal static async Task ReceiptsAsync(string root)
    {
        var paths = Paths(root); var bytes = Encoding.UTF8.GetBytes("verified content"); var requests = 0;
        using var http = new HttpClient(new FakeHandler(_ => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
        var file = new DownloadFile("https://resources.download.minecraft.net/fixture", "game/assets/file", Convert.ToHexString(SHA1.HashData(bytes)), bytes.Length);
        var downloads = new Downloads(http, paths);
        await downloads.EnsureAllAsync([file], new InlineProgress(), default);
        var index = paths.FilePath("verified-files.json"); var modified = File.GetLastWriteTimeUtc(index);
        await downloads.EnsureAllAsync([file], new InlineProgress(), default);
        Check(requests == 1 && downloads.ReceiptWrites == 1 && File.GetLastWriteTimeUtc(index) == modified, "Warm verification performs no download or receipt rewrite");
        var target = AppPaths.SafeChild(paths.Root, file.RelativePath);
        var verifiedModified = File.GetLastWriteTimeUtc(target);
        File.WriteAllText(target, "tampered content");
        // Some CI filesystems can retain the same timestamp for two immediate, same-size writes.
        File.SetLastWriteTimeUtc(target, verifiedModified.AddSeconds(2));
        await downloads.EnsureAllAsync([file], new InlineProgress(), default);
        Check(requests == 2 && File.ReadAllBytes(target).SequenceEqual(bytes), "A changed file is repaired");
        await Reject<OperationCanceledException>(() => downloads.EnsureAllAsync([file], new InlineProgress(), new CancellationToken(true)));
        var plan = new InstallPlan { Version = "fixture", MainClass = "old.Main", Files = [file] };
        JsonStore.Write(paths.FilePath("plans/fixture.json"), plan); var minecraft = new Minecraft(http, paths);
        await minecraft.PrepareAsync("fixture", new InlineProgress(), default);
        plan.MainClass = "changed.NewMain"; JsonStore.Write(paths.FilePath("plans/fixture.json"), plan);
        Check((await minecraft.PrepareAsync("fixture", new InlineProgress(), default)).MainClass == "changed.NewMain", "A changed plan invalidates the memory cache");
        minecraft.ReleasePreparationCache();
        Check((await minecraft.PrepareAsync("fixture", new InlineProgress(), default)).MainClass == "changed.NewMain", "Preparation works after releasing game-time caches");
    }
    internal static async Task JavaAsync(string root)
    {
        var paths = Paths(root); var plan = new InstallPlan { JavaRelativePath = "runtime/java-runtime-delta/bin/javaw.exe", JavaMajorVersion = 21 };
        var file = paths.FilePath(plan.JavaRelativePath); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = new byte[256]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(64)); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664); File.WriteAllBytes(file, bytes);
        var release = paths.FilePath("runtime/java-runtime-delta/release"); File.WriteAllText(release, "JAVA_VERSION=\"21.0.9\"\n");
        Check(JavaRuntimeInfo.Inspect(paths, plan) is { Major: 21, Architecture: "x64" }, "Java is checked from local metadata and PE headers");
        plan.JavaMajorVersion = 17; await Reject<InvalidDataException>(() => Task.Run(() => JavaRuntimeInfo.Inspect(paths, plan)));
        plan.JavaMajorVersion = 21; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x14c); File.WriteAllBytes(file, bytes);
        await Reject<InvalidDataException>(() => Task.Run(() => JavaRuntimeInfo.Inspect(paths, plan)));
        foreach (var flag in new[] { "-javaagent:helper.jar", "-agentpath:C:/helper.dll", "-agentlib:jdwp", "@other.args", "-Xrunjdwp:..." })
            await Reject<InvalidDataException>(() => Task.Run(() => JavaRuntimeInfo.ValidateArguments([flag])));
        JavaRuntimeInfo.ValidateArguments(["-Djava.library.path=C:/natives", "-cp", "libraries.jar"]);
        var report = WindowsGraphics.Read(file); Check(report.Adapters.Count <= 16, "Native GPU inspection completes without WMI, processes or registry changes");
    }
    internal static async Task NoInjectionAsync(string root)
    {
        // Launch the normal short-lived test child through the actual production process path.
        var paths = new AppPaths(AppContext.BaseDirectory); var library = new SkinLibrary(paths);
        var previous = library.Selected(); var local = library.Import(SkinProfileTests.Fixture(Paths(root)), true); library.Select(local);
        var supportExisted = Directory.Exists(paths.FilePath("support"));
        try
        {
            using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Launch must not download a helper or contact a skin server")));
            var minecraft = new Minecraft(http, paths);
            var id = "no-injection-" + Guid.NewGuid().ToString("N");
            var plan = new InstallPlan { Version = id, GameRelativeDirectory = "instances/" + id, JavaRelativePath = "Eclipse.Tests.exe", MainClass = "fixture", GameArguments = ["--eclipse-playtime-fixture-long"] };
            var protectedFiles = new[] { "mods/existing.jar", "config/existing.toml", "options.txt", "resourcepacks/existing.zip" };
            foreach (var relative in protectedFiles)
            {
                var path = AppPaths.SafeChild(paths.Root, plan.GameRelativeDirectory + "/" + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "preserve " + relative);
            }
            var exit = await minecraft.LaunchOfflineAsync(plan, OfflineAccount.Create("Local_Test"), 1536, new InlineProgress(), default);
            Check(exit == 0, "The selected preview skin does not prevent a plain local launch");
            var args = File.ReadAllText(paths.FilePath("game/versions/" + plan.Version + "/jvm.args"));
            Check(!args.Contains("javaagent") && Directory.Exists(paths.FilePath("support")) == supportExisted, "No agent, helper or local texture server is installed");
            Check(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(paths.FilePath("diagnostics/launch.json"))).GetProperty("state").GetString() == "exited", "Local measurements follow process exit");
            var report = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(paths.FilePath("diagnostics/launch.json")));
            Check(report.GetProperty("resourcesDuringGame").ValueKind == JsonValueKind.Object, "The one-shot game-time resource/GPU sample is saved before exit");
            foreach (var relative in protectedFiles)
                Check(File.ReadAllText(AppPaths.SafeChild(paths.Root, plan.GameRelativeDirectory + "/" + relative)) == "preserve " + relative, "Game configuration/mod/resource-pack bytes stay unchanged");
        }
        finally { library.Select(previous); }
    }
    internal static Task BackdropAsync()
    {
        var method = typeof(LauncherBackdrop).GetMethod("Create", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var (size, expected) in new[] {
            (new System.Drawing.Size(1040, 720), "9971285F04B8B35B2D019E4E77873EAC290619BEA59D62789DD3F27D831048B4"),
            (new System.Drawing.Size(1280, 850), "9D88207EF39C1D2367DD4E43CC30136FD2FEAACD097086E776B4D87FD5647013") })
        {
            using var image = (System.Drawing.Bitmap)method.Invoke(null, [size, 600])!;
            var bits = image.LockBits(new System.Drawing.Rectangle(System.Drawing.Point.Empty, size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var raw = new byte[bits.Stride * size.Height]; System.Runtime.InteropServices.Marshal.Copy(bits.Scan0, raw, 0, raw.Length);
                Check(Convert.ToHexString(SHA256.HashData(raw)) == expected, "Optimized noise/gradient pixels match the original 0.4.0 rendering");
            }
            finally { image.UnlockBits(bits); }
        }
        return Task.CompletedTask;
    }
}
