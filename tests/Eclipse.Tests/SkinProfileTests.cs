using System.Drawing.Imaging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eclipse;

internal static class SkinProfileTests
{
    private static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static AppPaths Paths(string root) => new(Path.Combine(root, "skin-profiles", Guid.NewGuid().ToString("N")));
    internal static string Fixture(AppPaths paths)
    {
        var file = paths.FilePath("input.png"); Directory.CreateDirectory(paths.Root);
        using var skin = SkinScene.CreateDefaultSkin(); skin.Save(file, ImageFormat.Png); return file;
    }
    internal static async Task LibraryAsync(string root)
    {
        var paths = Paths(root); var library = new SkinLibrary(paths); var file = Fixture(paths);
        var classic = library.Import(file, false); var slim = library.Import(file, true);
        Check(classic.Id != slim.Id && classic.Variant == "CLASSIC" && slim.Variant == "SLIM");
        Check(library.Import(file, false) == classic && library.All().Count == 2, "Imports are deduplicated");
        library.Select(slim); File.Delete(file);
        Check(new SkinLibrary(paths).Selected() == slim, "Selection survives restart without the original PNG");
        using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Local skins must not use HTTP")));
        using var bitmap = await new AppearanceService(http, paths).TextureAsync(slim.Texture(true), default);
        Check(bitmap.Width == 64 && bitmap.Height == 64);
        await Reject<InvalidDataException>(() => Task.Run(() => library.FilePath(classic with { Id = "../../escape" })));
        File.WriteAllBytes(file, [1, 2, 3]);
        await Reject<InvalidDataException>(() => Task.Run(() => library.Import(file, false)));
        using (var wrong = new Bitmap(128, 128)) wrong.Save(file, ImageFormat.Png);
        await Reject<InvalidDataException>(() => Task.Run(() => library.Import(file, false)));
        using (var legacy = new Bitmap(64, 32)) legacy.Save(file, ImageFormat.Png);
        Check(library.Import(file, false).Variant == "CLASSIC");
        await Reject<InvalidDataException>(() => Task.Run(() => library.Import(file, true)));
        File.WriteAllBytes(file, new byte[2 * 1024 * 1024 + 1]);
        await Reject<InvalidDataException>(() => Task.Run(() => library.Import(file, false)));
        library.Select(null); Check(new SkinLibrary(paths).Selected() == null);
    }
    internal static async Task UploadAsync(string root)
    {
        var paths = Paths(root); var skin = new SkinLibrary(paths).Import(Fixture(paths), true);
        using var auth = new HttpClient(new FakeHandler(r => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r.RequestUri!.AbsolutePath.Contains("entitlements") ? """{"items":[{"name":"game_minecraft"}]}""" : """{"id":"0123456789abcdef0123456789abcdef","name":"Fixture"}""") })));
        var account = await LicensedAccount.VerifyAsync(auth, "secret-test-token", default);
        var posts = 0; var gets = 0;
        using var http = new HttpClient(new FakeHandler(async request =>
        {
            Check(request.Headers.Authorization?.ToString() == "Bearer secret-test-token");
            Check(request.RequestUri!.Host == "api.minecraftservices.com");
            if (request.Method == HttpMethod.Post)
            {
                posts++; Check(request.RequestUri.AbsolutePath.EndsWith("/skins"));
                var form = request.Content as MultipartFormDataContent ?? throw new Exception("Expected multipart upload");
                var parts = form.ToArray(); Check(parts.Length == 2);
                Check(await parts[0].ReadAsStringAsync() == "slim");
                Check(parts[1].Headers.ContentType?.MediaType == "image/png");
                Check((await parts[1].ReadAsByteArrayAsync()).SequenceEqual(new SkinLibrary(paths).Bytes(skin)));
                return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            gets++; Check(request.Method == HttpMethod.Get);
            return new(HttpStatusCode.OK) { Content = new StringContent(account.Profile.GetRawText()) };
        }));
        var service = new AppearanceService(http, paths);
        var updated = await service.ApplyAsync(account, skin.Texture(false), null, default);
        Check(posts == 1 && gets == 1 && updated.Uuid == account.Uuid);
        await Reject<InvalidOperationException>(() => service.ApplyAsync(account, skin.Texture(false), new("foreign", "", "", "", false), default));
        Check(posts == 1, "A foreign cape is rejected before mutating the skin");
        Check(service.Library.All().Single() == skin, "Uploading keeps the reusable local PNG");
        service.Library.Select(skin);
        var plan = new InstallPlan { Version = "fixture", JavaRelativePath = "runtime/java.exe", MainClass = "fixture.Main" };
        var info = await new Minecraft(http, paths).CreateStartInfoAsync(plan, account, 2048, default);
        Check(!File.ReadAllText(info.ArgumentList[0][1..]).Contains("javaagent"), "Microsoft JVM arguments never load the local skin agent");
    }
    internal static async Task CopiesAsync(string root)
    {
        var paths = Paths(root); var manager = new GameProfiles(paths);
        Directory.CreateDirectory(Path.Combine(paths.SharedGame, "mods"));
        Directory.CreateDirectory(Path.Combine(paths.SharedGame, "saves", "World"));
        File.WriteAllText(Path.Combine(paths.SharedGame, "mods", "example.jar"), "original");
        File.WriteAllText(Path.Combine(paths.SharedGame, "options.txt"), "options");
        File.WriteAllText(Path.Combine(paths.SharedGame, "saves", "World", "level.dat"), "world");
        var alias = manager.Rename("26.3", "Vanilla / base");
        Check(alias.SharedDirectory && alias.Id == "26.3", "Renaming vanilla preserves its existing game folder");
        var first = await manager.CopyAsync("26.3", "Pack One", true, false, new InlineProgress(), default);
        var second = await manager.CopyAsync(first.Id, "Pack Two", true, true, new InlineProgress(), default);
        Check(first.Id != second.Id && first.GameVersion == "26.3" && second.SourceVersion == "26.3");
        var firstDir = AppPaths.SafeChild(paths.Root, first.GameDirectory);
        Check(File.Exists(Path.Combine(firstDir, "mods", "example.jar")) && File.Exists(Path.Combine(firstDir, "options.txt")));
        Check(!Directory.Exists(Path.Combine(firstDir, "saves")), "World copying is opt-in");
        File.WriteAllText(Path.Combine(firstDir, "mods", "example.jar"), "changed");
        Check(File.ReadAllText(AppPaths.SafeChild(paths.Root, second.GameDirectory + "/mods/example.jar")) == "original");
        Check(File.ReadAllText(Path.Combine(paths.SharedGame, "mods", "example.jar")) == "original");
        var withWorlds = await manager.CopyAsync("26.3", "World copy", false, true, new InlineProgress(), default);
        Check(File.Exists(AppPaths.SafeChild(paths.Root, withWorlds.GameDirectory + "/saves/World/level.dat")));
        var renamed = manager.Rename(first.Id, "Пакет renamed");
        Check(renamed.GameDirectory == first.GameDirectory && renamed.SourceVersion == first.SourceVersion);
        Check(ModdedProfile.ReadAll(paths).First(p => p.Id == first.Id).Name == "Пакет renamed");
        var plan = new InstallPlan { Version = "26.3", MainClass = "fixture.Main", JavaRelativePath = "runtime/java.exe", ClassPath = ["game/libraries/fixture.jar"] };
        JsonStore.Write(paths.FilePath("plans/26.3.json"), plan);
        using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Copy preparation must reuse installed libraries")));
        var prepared = await new Minecraft(http, paths).PrepareAsync(first.Id, new InlineProgress(), default);
        Check(prepared.BaseVersion == "26.3" && prepared.GameRelativeDirectory == first.GameDirectory && prepared.ClassPath.SequenceEqual(plan.ClassPath));
        var fabric = new ModdedProfile("fabric-loader-1.2.3-26.3", "26.3", "fabric", "1.2.3");
        JsonStore.Write(paths.FilePath("profiles.json"), ModdedProfile.ReadAll(paths).Append(fabric).ToList());
        var fabricCopy = await manager.CopyAsync(fabric.Id, "Fabric pack", false, false, new InlineProgress(), default);
        Check(fabricCopy.SourceVersion == fabric.Id && fabricCopy.Loader == "fabric" && fabricCopy.LoaderVersion == "1.2.3");
        var copyPlan = GameProfiles.InstancePlan(plan, renamed);
        Check(copyPlan.BaseVersion == "26.3" && copyPlan.GameRelativeDirectory == first.GameDirectory && copyPlan.ClassPath.SequenceEqual(plan.ClassPath));
        Check((await new Minecraft(http, paths).PrepareAsync(first.Id, new InlineProgress(), default)).Version == first.Id);
        await Reject<InvalidOperationException>(() => Task.Run(() => manager.Rename(first.Id, "  ")));
        await Reject<InvalidDataException>(() => Task.Run(() => GameProfiles.ResolveRuntime("a", [new("a", "26.3", "vanilla", "") { SourceVersion = "a" }])));
        var before = ModdedProfile.ReadAll(paths).Count;
        await Reject<OperationCanceledException>(() => manager.CopyAsync("26.3", "Cancelled", true, true, new InlineProgress(), new CancellationToken(true)));
        Check(ModdedProfile.ReadAll(paths).Count == before && !Directory.GetDirectories(paths.FilePath("instances"), ".creating-*").Any());
    }
}
