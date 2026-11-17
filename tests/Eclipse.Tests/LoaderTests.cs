using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eclipse;

internal static class LoaderTests
{
    internal static async Task InstallAsync(string loader, bool inlineChecksums, string root)
    {
        var paths = new AppPaths(Path.Combine(root, "loaders", Guid.NewGuid().ToString("N")));
        var native = paths.FilePath("game/versions/fixture/natives/test.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!); File.WriteAllBytes(native, [1]);
        JsonStore.Write(paths.FilePath("plans/fixture.json"), new InstallPlan
        {
            Version = "fixture", JavaRelativePath = "runtime/java.exe", MainClass = "vanilla.Main", AssetIndex = "fixture-assets",
            ClassPath = ["game/libraries/test/loader/client/old/client-old.jar", "game/versions/fixture/fixture.jar"],
            NativeFiles = ["game/versions/fixture/natives/test.dll"],
            JvmArguments = ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
            GameArguments = ["--username", "${auth_player_name}", "--gameDir", "${game_directory}", "--assetsDir", "${assets_root}"]
        });
        var bytes = Encoding.UTF8.GetBytes("verified loader fixture");
        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var library = new Dictionary<string, object> { ["name"] = "test.loader:client:1.2.3", ["url"] = "https://maven.fabricmc.net/" };
        if (inlineChecksums) { library["sha1"] = hash; library["size"] = bytes.Length; }
        var id = loader + "-loader-1.2.3-fixture";
        var json = JsonSerializer.Serialize(new
        {
            id, inheritsFrom = "fixture", mainClass = loader == "fabric" ? "net.fabricmc.loader.impl.launch.knot.KnotClient" : "org.quiltmc.loader.impl.launch.knot.KnotClient",
            arguments = new { jvm = new[] { "-Dloader.fixture=true" }, game = Array.Empty<string>() }, libraries = new[] { library }
        });
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath.EndsWith("profile/json")) return new(HttpStatusCode.OK) { Content = new StringContent(json) };
            if (request.RequestUri.AbsolutePath.EndsWith(".sha1")) return new(HttpStatusCode.OK) { Content = new StringContent(hash + "\n") };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var minecraft = new Minecraft(http, paths);
        var loaders = new ModLoaders(http, paths, minecraft);
        var progress = new Progress<string>();
        var plan = await loaders.InstallAsync(loader, "fixture", "1.2.3", progress, default);
        Check(plan.Version == id && plan.BaseVersion == "fixture" && plan.GameRelativeDirectory == "instances/" + id);
        Check(plan.JavaRelativePath == "runtime/java.exe" && plan.AssetIndex == "fixture-assets");
        Check(plan.ClassPath.Contains("game/libraries/test/loader/client/1.2.3/client-1.2.3.jar") && !plan.ClassPath.Any(p => p.Contains("client-old.jar")));
        Check(Directory.Exists(paths.FilePath("instances/" + id + "/mods")) && ModdedProfile.ReadAll(paths).Single().Id == id);
        var beforeWarm = requests;
        await minecraft.PrepareAsync(id, progress, default);
        Check(requests == beforeWarm);
        paths.UseGameDirectory("instances/" + id);
        var info = await minecraft.CreateOfflineStartInfoAsync(plan, OfflineAccount.Create("Loader_Test"), 4096, default);
        var gameDir = info.ArgumentList[info.ArgumentList.IndexOf("--gameDir") + 1];
        var assetsDir = info.ArgumentList[info.ArgumentList.IndexOf("--assetsDir") + 1];
        Check(gameDir == paths.Game && assetsDir == Path.Combine(paths.SharedGame, "assets"));
        var jvm = await File.ReadAllTextAsync(info.ArgumentList[0][1..]);
        var nativeArgument = Minecraft.QuoteJavaArgument("-Djava.library.path=" + AppPaths.SafeChild(paths.Root, "game/versions/fixture/natives"));
        Check(jvm.Contains(nativeArgument) && jvm.Contains("-Dloader.fixture=true") && jvm.Contains(plan.MainClass));
    }
    private static void Check(bool valid) { if (!valid) throw new InvalidOperationException("Loader fixture assertion failed"); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(action(request));
    }
}
