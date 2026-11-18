using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eclipse;

if (args.Contains("--eclipse-playtime-fixture")) { await Task.Delay(250); return 0; }
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.test-data"));
Directory.CreateDirectory(root);
if (args is ["--download-launcher-update", var runningVersion, var updateDirectory])
{
    using var updateHttp = Network.Create();
    var service = new LauncherUpdates(updateHttp);
    var release = await service.CheckAsync(default, Version.Parse(runningVersion));
    if (release == null) { Console.WriteLine("No newer stable release"); return 1; }
    var installer = await service.DownloadAsync(release, updateDirectory, new Progress<int>(), default);
    Console.WriteLine(JsonSerializer.Serialize(new { version = release.Version.ToString(3), installer, release.Sha256 }));
    return 0;
}
if (args is ["--start-launcher-update", var updateInstaller, var updateAppDirectory, var updateDataDirectory])
{
    var updateStart = LauncherUpdates.InstallerStartInfo(updateInstaller,
        Path.TrimEndingDirectorySeparator(updateAppDirectory) + Path.DirectorySeparatorChar,
        Path.TrimEndingDirectorySeparator(updateDataDirectory) + Path.DirectorySeparatorChar, Environment.ProcessId);
    updateStart.ArgumentList.Add("/ECLIPSETESTINSTALL=1");
    updateStart.ArgumentList.Add("/NOICONS");
    updateStart.ArgumentList.Add("/TASKS=");
    updateStart.ArgumentList.Add("/LOG=" + Path.Combine(updateAppDirectory, "automatic-update.log"));
    using var setup = Process.Start(updateStart);
    if (setup == null) return 1;
    Console.WriteLine("Installer PID " + setup.Id);
    return 0;
}
if (args is ["--ui", var screenshotDirectory]) return UiTests.Run(root, screenshotDirectory);
if (args is ["--ui-en", var englishScreenshotDirectory]) return UiTests.Run(root, englishScreenshotDirectory, "en");
if (args is ["--offline-launch-check", var gameDataDirectory, var launchCheckDirectory])
    return await OfflineLaunchDiagnostics.RunAsync(gameDataDirectory, launchCheckDirectory);
if (args is ["--offline-launch-check", var loaderDataDirectory, var loaderCheckDirectory, var installedVersion])
    return await OfflineLaunchDiagnostics.RunAsync(loaderDataDirectory, loaderCheckDirectory, installedVersion);
if (args is ["--prepare-loader", var loaderName, var baseVersion, var loaderVersion, var loaderDirectory])
{
    using var http = Network.Create();
    var paths = new AppPaths(loaderDirectory);
    var minecraft = new Minecraft(http, paths);
    var service = new ModLoaders(http, paths, minecraft);
    if (loaderVersion == "latest") loaderVersion = (await service.VersionsAsync(loaderName, baseVersion, default)).First().Version;
    var timer = Stopwatch.StartNew();
    var plan = await service.InstallAsync(loaderName, baseVersion, loaderVersion, new InlineProgress(Console.WriteLine), default);
    var installedMs = timer.Elapsed.TotalMilliseconds;
    timer.Restart();
    await minecraft.PrepareAsync(plan.Version, new InlineProgress(), default);
    Console.WriteLine(JsonSerializer.Serialize(new { profile = plan.Version, plan.MainClass, files = plan.Files.Count, installedMs, cachedPrepareMs = timer.Elapsed.TotalMilliseconds }));
    return 0;
}
if (args is ["--audio-check"])
{
    UiSounds.Enabled = true;
    UiSounds.Start();
    try
    {
        await Task.Delay(150);
        foreach (var cue in Enum.GetValues<UiCue>()) { UiSounds.Play(cue); await Task.Delay(130); }
        var accepted = UiSounds.PlayedCount;
        Console.WriteLine($"Windows audio: {accepted} sounds accepted by PlaySound");
        return accepted > 0 ? 0 : 1;
    }
    finally { UiSounds.Stop(); }
}
if (args is ["--verify-account", var accountConfiguration, var accountDataDirectory])
    return await AccountDiagnostics.VerifyAsync(accountConfiguration, accountDataDirectory);
if (args is ["--check-login", var configurationPath])
{
    var config = JsonStore.Read<LauncherConfig>(configurationPath) ?? throw new InvalidOperationException("Configuration not found");
    var callback = new Uri("http://localhost:54321/");
    var parameters = new Dictionary<string, string>
    {
        ["client_id"] = config.MicrosoftClientId, ["redirect_uri"] = callback.AbsoluteUri, ["response_type"] = "code",
        ["scope"] = "XboxLive.signin offline_access", ["state"] = Authentication.Base64Url(RandomNumberGenerator.GetBytes(32)),
        ["code_challenge"] = Authentication.Challenge(Authentication.Base64Url(RandomNumberGenerator.GetBytes(32))), ["code_challenge_method"] = "S256"
    };
    var uri = new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
    using var http = OAuthRegistration.CreateClient();
    try { await OAuthRegistration.ValidateAsync(http, uri, callback, default); Console.WriteLine("PASS Microsoft registration check: no redirect_uri rejection; no account sign-in was performed."); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
var progress = new InlineProgress();
if (args is ["--prepare-only", var version, var dataDirectory])
{
    using var http = Network.Create();
    var minecraft = new Minecraft(http, new AppPaths(dataDirectory));
    var watch = Stopwatch.StartNew();
    var plan = await minecraft.PrepareAsync(version, new InlineProgress(Console.WriteLine), CancellationToken.None);
    var installed = watch.Elapsed;
    watch.Restart();
    await minecraft.PrepareAsync(version, progress, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { version, files = plan.Files.Count, bytes = plan.Files.DistinctBy(f => f.RelativePath).Sum(f => f.Size), firstPrepareSeconds = installed.TotalSeconds, cachedPrepareMs = watch.Elapsed.TotalMilliseconds }));
    var java = Path.Combine(dataDirectory, plan.JavaRelativePath.Replace("javaw.exe", "java.exe"));
    using var process = Process.Start(new ProcessStartInfo(java, "-version") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true })!;
    Console.WriteLine(await process.StandardError.ReadToEndAsync());
    await process.WaitForExitAsync();
    return process.ExitCode;
}

if (args.Length != 0) { Console.Error.WriteLine("Unknown test arguments"); return 2; }
var initialLanguage = L.Language;
L.SetLanguage("ru");
var passed = 0;
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex); Environment.Exit(1); }
}
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
AppPaths NewPaths() => new(Path.Combine(root, "cases", Guid.NewGuid().ToString("N")));
JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
const string profileJson = "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"LicensedPlayer\"}";
const string licenseJson = "{\"items\":[{\"name\":\"game_minecraft\"}]}";
const string clientId = "12345678-1234-1234-1234-123456789abc";

await Test("PKCE matches RFC 7636 S256 vector", () =>
{
    Check(Authentication.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    return Task.CompletedTask;
});
await Test("Unrelated products and empty entitlements are rejected", () =>
{
    foreach (var json in new[] { "{}", "{\"items\":[]}", "{\"items\":[{\"name\":\"product_minecraft_bedrock\"}]}" })
        Check(!LicensedAccount.HasLicense(Json(json)));
    Check(LicensedAccount.HasLicense(Json(licenseJson)));
    Check(LicensedAccount.HasLicense(Json("{\"items\":[{\"name\":\"product_minecraft\"}]}")));
    return Task.CompletedTask;
});
await Test("Profile alone cannot authorize a game", async () =>
{
    using var http = new HttpClient(new FakeHandler(r => Task.FromResult(Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? "{\"items\":[]}" : profileJson))));
    await Throws<InvalidOperationException>(() => LicensedAccount.VerifyAsync(http, "test-token", default));
});
await Test("An HTTP license error fails closed", async () =>
{
    using var http = new HttpClient(new FakeHandler(r => Task.FromResult(Reply("{}", HttpStatusCode.ServiceUnavailable))));
    await Throws<ServiceException>(() => LicensedAccount.VerifyAsync(http, "test-token", default));
});
await Test("A network outage fails closed", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("Network unavailable")));
    await Throws<HttpRequestException>(() => LicensedAccount.VerifyAsync(http, "test-token", default));
});
await Test("License and real profile are both required", async () =>
{
    var calls = 0;
    using var http = new HttpClient(new FakeHandler(r =>
    {
        Interlocked.Increment(ref calls);
        Check(r.Headers.Authorization?.ToString() == "Bearer test-token");
        return Task.FromResult(Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? licenseJson : profileJson));
    }));
    var account = await LicensedAccount.VerifyAsync(http, "test-token", default);
    Check(account.Name == "LicensedPlayer" && account.Uuid.Length == 32 && calls == 2);
});
await Test("Invalid Minecraft profile is rejected", async () =>
{
    using var http = new HttpClient(new FakeHandler(r => Task.FromResult(Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? licenseJson : "{\"name\":\"Player\",\"id\":\"fake-uuid\"}"))));
    await Throws<InvalidDataException>(() => LicensedAccount.VerifyAsync(http, "test-token", default));
});
await Test("Microsoft sign-in still requires its application registration", async () =>
{
    var calls = 0;
    using var http = new HttpClient(new FakeHandler(_ => { calls++; throw new Exception("Unexpected HTTP"); }));
    var auth = new Authentication(http, NewPaths(), "");
    await Throws<InvalidOperationException>(() => auth.AuthenticateAsync(false, progress, default));
    Check(calls == 0);
});
await Test("DPAPI token cache has no plaintext and round-trips", () =>
{
    var file = NewPaths().FilePath("secret.bin");
    SecretStore.Save(file, new Session { RefreshToken = "secret-refresh-token", MinecraftToken = "secret-minecraft-token" });
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("secret-"));
    Check(SecretStore.Load<Session>(file)?.RefreshToken == "secret-refresh-token");
    return Task.CompletedTask;
});
await Test("Every warm launch rechecks server license and profile", async () =>
{
    var paths = NewPaths();
    SecretStore.Save(paths.FilePath("session.bin"), new Session { ClientId = clientId, MinecraftToken = "cached-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
    var calls = 0;
    using var http = new HttpClient(new FakeHandler(r =>
    {
        Interlocked.Increment(ref calls);
        return Task.FromResult(Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? licenseJson : profileJson));
    }));
    var auth = new Authentication(http, paths, clientId);
    await auth.AuthenticateAsync(false, progress, default);
    await auth.AuthenticateAsync(false, progress, default);
    Check(calls == 4);
    auth.SignOut();
    Check(!File.Exists(paths.FilePath("session.bin")));
});
await Test("Expired session refreshes Microsoft, Xbox, XSTS, Minecraft and license", async () =>
{
    var paths = NewPaths();
    SecretStore.Save(paths.FilePath("session.bin"), new Session { ClientId = clientId, RefreshToken = "old-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
    var calls = new List<string>();
    using var http = new HttpClient(new FakeHandler(async r =>
    {
        var path = r.RequestUri!.AbsolutePath;
        lock (calls) calls.Add(path);
        if (path.EndsWith("/token"))
        {
            var body = await r.Content!.ReadAsStringAsync();
            Check(body.Contains("grant_type=refresh_token") && body.Contains("old-refresh") && !body.Contains("client_secret"));
            return Reply("{\"access_token\":\"ms-token\",\"refresh_token\":\"new-refresh\"}");
        }
        if (path is "/user/authenticate" or "/xsts/authorize")
        {
            var payload = Json(await r.Content!.ReadAsStringAsync());
            Check(payload.TryGetProperty("Properties", out var properties), "Xbox requires case-sensitive 'Properties'; camelCase is rejected with HTTP 400");
            Check(payload.Text("TokenType") == "JWT");
            Check(r.Headers.GetValues("x-xbl-contract-version").Single() == "1");
            if (path == "/user/authenticate")
            {
                Check(payload.Text("RelyingParty") == "http://auth.xboxlive.com");
                Check(properties.Text("AuthMethod") == "RPS" && properties.Text("SiteName") == "user.auth.xboxlive.com");
                Check(properties.Text("RpsTicket") == "d=ms-token");
            }
            else
            {
                Check(payload.Text("RelyingParty") == "rp://api.minecraftservices.com/");
                Check(properties.Text("SandboxId") == "RETAIL" && properties.GetProperty("UserTokens")[0].GetString() == "xbox-token");
            }
            return Reply("{\"Token\":\"xbox-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"test-hash\"}]}}");
        }
        if (path == "/authentication/login_with_xbox")
        {
            var payload = Json(await r.Content!.ReadAsStringAsync());
            Check(payload.Text("identityToken") == "XBL3.0 x=test-hash;xbox-token");
            Check(!payload.TryGetProperty("IdentityToken", out _), "Minecraft's lower-case contract must also remain unchanged");
            return Reply("{\"access_token\":\"minecraft-token\",\"expires_in\":86400}");
        }
        return Reply(path.Contains("entitlements") ? licenseJson : profileJson);
    }));
    var account = await new Authentication(http, paths, clientId).AuthenticateAsync(false, progress, default);
    Check(account.Name == "LicensedPlayer" && calls.Count == 6);
    Check(SecretStore.Load<Session>(paths.FilePath("session.bin"))?.RefreshToken == "new-refresh");
});
await Test("Revoked refresh credentials are cleared", async () =>
{
    var paths = NewPaths();
    SecretStore.Save(paths.FilePath("session.bin"), new Session { ClientId = clientId, RefreshToken = "revoked" });
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Reply("{}", HttpStatusCode.BadRequest))));
    await Throws<InvalidOperationException>(() => new Authentication(http, paths, clientId).AuthenticateAsync(false, progress, default));
    Check(!File.Exists(paths.FilePath("session.bin")));
});
await Test("OS rules choose Windows x64 and suppress demo flags", () =>
{
    Check(Minecraft.Allowed(Json("{}")));
    Check(Minecraft.Allowed(Json("{\"rules\":[{\"action\":\"allow\",\"os\":{\"name\":\"windows\"}}]}")));
    Check(!Minecraft.Allowed(Json("{\"rules\":[{\"action\":\"allow\",\"os\":{\"name\":\"osx\"}}]}")));
    Check(!Minecraft.Allowed(Json("{\"rules\":[{\"action\":\"allow\",\"os\":{\"arch\":\"x86\"}}]}")));
    var selected = Minecraft.SelectArguments(Json("[\"--username\",\"${auth_player_name}\",{\"rules\":[{\"action\":\"allow\",\"features\":{\"is_demo_user\":true}}],\"value\":\"--demo\"}]"));
    Check(selected.Count == 2 && !selected.Contains("--demo"));
    Check(!Minecraft.Allowed(Json("{\"rules\":[{\"action\":\"allow\"},{\"action\":\"disallow\",\"os\":{\"name\":\"windows\"}}]}")));
    return Task.CompletedTask;
});
await Test("OS version range boundaries are applied", () =>
{
    Check(Minecraft.Allowed(Json("{\"rules\":[{\"action\":\"allow\",\"os\":{\"versionRange\":{\"min\":\"10.0\"}}}]}")));
    Check(!Minecraft.Allowed(Json("{\"rules\":[{\"action\":\"allow\",\"os\":{\"versionRange\":{\"max\":\"10.0\"}}}]}")));
    return Task.CompletedTask;
});
await Test("Cache and archive paths cannot escape the data directory", async () =>
{
    var paths = NewPaths();
    foreach (var path in new[] { "../outside", "x/../../outside", "C:/Windows/test", "/Windows/test" })
        await Throws<InvalidDataException>(() => Task.FromResult(AppPaths.SafeChild(paths.Root, path)));
    Check(AppPaths.SafeChild(paths.Root, "game/libraries/a.jar").StartsWith(paths.Root));
});
await Test("JVM argument files quote spaces, backslashes and quotes", () =>
{
    Check(Minecraft.QuoteJavaArgument("C:\\Game Files\\a.jar") == "\"C:\\\\Game Files\\\\a.jar\"");
    Check(Minecraft.QuoteJavaArgument("x\"y") == "\"x\\\"y\"");
    return Task.CompletedTask;
});
await Test("Download is verified once; warm preparation uses receipts without HTTP", async () =>
{
    var paths = NewPaths();
    var bytes = Encoding.UTF8.GetBytes("official-test-content");
    var sha = Convert.ToHexString(SHA1.HashData(bytes));
    var file = new DownloadFile("https://libraries.minecraft.net/test.jar", "game/test.jar", sha, bytes.Length);
    var calls = 0;
    using var http = new HttpClient(new FakeHandler(_ => { Interlocked.Increment(ref calls); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
    var downloader = new Downloads(http, paths);
    await downloader.EnsureAllAsync([file, file], progress, default);
    await new Downloads(http, paths).EnsureAllAsync([file], progress, default);
    Check(calls == 1);
    var target = AppPaths.SafeChild(paths.Root, file.RelativePath);
    await File.WriteAllBytesAsync(target, new byte[bytes.Length]);
    File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
    await new Downloads(http, paths).EnsureAllAsync([file], progress, default);
    Check(calls == 2 && File.ReadAllBytes(target).SequenceEqual(bytes));
});
await Test("A damaged download is never installed", async () =>
{
    var paths = NewPaths();
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })));
    var file = new DownloadFile("https://libraries.minecraft.net/test.jar", "game/test.jar", new string('0', 40), 3);
    await Throws<InvalidDataException>(() => new Downloads(http, paths).EnsureAsync(file, default));
    Check(!File.Exists(paths.FilePath("game/test.jar")) && !File.Exists(paths.FilePath("game/test.jar.part")));
});
await Test("Untrusted download hosts are refused before HTTP", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Unexpected HTTP")));
    await Throws<InvalidDataException>(() => new Downloads(http, NewPaths()).EnsureAsync(new("https://example.com/java.exe", "runtime/java.exe", new string('0', 40), 10), default));
});
await Test("Launch arguments use verified identity and never persist the access token", async () =>
{
    using var http = new HttpClient(new FakeHandler(r => Task.FromResult(Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? licenseJson : profileJson))));
    var account = await LicensedAccount.VerifyAsync(http, "sensitive-test-token", default, clientId, "1234");
    var paths = NewPaths();
    var plan = new InstallPlan
    {
        Version = "26.3", MainClass = "net.minecraft.client.main.Main", JavaRelativePath = "runtime/bin/javaw.exe",
        ClassPath = ["game/libraries/path with spaces/library.jar", "game/versions/26.3/26.3.jar"],
        JvmArguments = ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
        GameArguments = ["--username", "${auth_player_name}", "--uuid", "${auth_uuid}", "--accessToken", "${auth_access_token}", "--clientId", "${clientid}", "--xuid", "${auth_xuid}"]
    };
    var info = await new Minecraft(http, paths).CreateStartInfoAsync(plan, account, 4096, default);
    Check(!info.UseShellExecute && info.CreateNoWindow && info.RedirectStandardError);
    Check(info.ArgumentList.Contains("LicensedPlayer") && info.ArgumentList.Contains("sensitive-test-token"));
    Check(info.ArgumentList.Contains(clientId) && info.ArgumentList.Contains("1234"));
    Check(!info.Environment.ContainsKey("JAVA_TOOL_OPTIONS"));
    var argumentFile = info.ArgumentList[0][1..];
    var savedArgs = await File.ReadAllTextAsync(argumentFile);
    Check(!savedArgs.Contains("sensitive-test-token") && !savedArgs.Contains("LicensedPlayer"));
    Check(savedArgs.Contains("-Xms256M") && savedArgs.Contains("-Xmx4096M") && savedArgs.Contains("path with spaces"));
    Check(!info.ArgumentList.Contains("--demo"));
});
await Test("A complete cached installation prepares without network", async () =>
{
    var paths = NewPaths();
    var bytes = Encoding.UTF8.GetBytes("cached fixture");
    var relative = "game/versions/fixture/fixture.jar";
    var target = AppPaths.SafeChild(paths.Root, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    await File.WriteAllBytesAsync(target, bytes);
    var file = new DownloadFile("https://piston-data.mojang.com/fixture", relative, Convert.ToHexString(SHA1.HashData(bytes)), bytes.Length);
    JsonStore.Write(paths.FilePath("plans/fixture.json"), new InstallPlan { Version = "fixture", Files = [file] });
    using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Unexpected network during cached preparation")));
    var plan = await new Minecraft(http, paths).PrepareAsync("fixture", progress, default);
    Check(plan.Version == "fixture" && File.ReadAllBytes(target).SequenceEqual(bytes));
});
await Test("Text editor preserves Cyrillic, line endings and UTF BOMs", () =>
{
    var paths = NewPaths(); Directory.CreateDirectory(paths.Game);
    foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
    {
        var path = Path.Combine(paths.Game, "options.txt");
        const string original = "Привет\r\nrenderDistance:12\r\n";
        File.WriteAllText(path, original, encoding);
        var document = GameFiles.Read(paths.Game, path);
        var updated = GameFiles.Save(paths.Game, document, document.Text.Replace(":12", ":18"));
        Check(updated.Text == original.Replace(":12", ":18"));
        var bytes = File.ReadAllBytes(path);
        Check(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
    }
    return Task.CompletedTask;
});
await Test("Editor refuses an external path and a binary file", async () =>
{
    var paths = NewPaths(); Directory.CreateDirectory(paths.Game);
    var external = paths.FilePath("outside.txt"); File.WriteAllText(external, "outside");
    await Throws<InvalidDataException>(() => Task.Run(() => GameFiles.Read(paths.Game, external)));
    var binary = Path.Combine(paths.Game, "test.jar"); File.WriteAllBytes(binary, [80, 75, 0, 1, 2]);
    await Throws<IOException>(() => Task.Run(() => GameFiles.Read(paths.Game, binary)));
});
await Test("Editor refuses to overwrite a file changed by Minecraft", async () =>
{
    var paths = NewPaths(); Directory.CreateDirectory(paths.Game);
    var path = Path.Combine(paths.Game, "options.txt"); File.WriteAllText(path, "old");
    var document = GameFiles.Read(paths.Game, path);
    File.WriteAllText(path, "changed outside launcher");
    await Throws<IOException>(() => Task.Run(() => GameFiles.Save(paths.Game, document, "edited")));
    Check(File.ReadAllText(path) == "changed outside launcher");
});
await Test("Launch settings reach the JVM and game arguments", async () =>
{
    using var http = new HttpClient(new FakeHandler(r => Task.FromResult(Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? licenseJson : profileJson))));
    var account = await LicensedAccount.VerifyAsync(http, "test-token", default);
    var paths = NewPaths();
    var plan = new InstallPlan { Version = "26.3", MainClass = "net.minecraft.client.main.Main", JavaRelativePath = "runtime/bin/javaw.exe", JvmArguments = ["-cp", "${classpath}"] };
    var options = new Settings { MemoryMb = 6144, WindowWidth = 1920, WindowHeight = 1080, Fullscreen = true };
    var info = await new Minecraft(http, paths).CreateStartInfoAsync(plan, account, options.MemoryMb, default, options);
    Check(info.ArgumentList.Contains("--fullscreen") && info.ArgumentList.Contains("1920") && info.ArgumentList.Contains("1080"));
    Check((await File.ReadAllTextAsync(info.ArgumentList[0][1..])).Contains("-Xmx6144M"));
});
await Test("Oversized editor changes do not modify the original file", async () =>
{
    var paths = NewPaths(); Directory.CreateDirectory(paths.Game);
    var path = Path.Combine(paths.Game, "options.txt"); File.WriteAllText(path, "original");
    var document = GameFiles.Read(paths.Game, path);
    await Throws<IOException>(() => Task.Run(() => GameFiles.Save(paths.Game, document, new string('x', 2 * 1024 * 1024 + 1))));
    Check(File.ReadAllText(path) == "original");
});
await Test("Appearance parser distinguishes owned capes and active slim skin", () =>
{
    var profile = AppearanceProfile.Parse(Json("{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"LicensedPlayer\",\"skins\":[{\"id\":\"skin\",\"url\":\"https://textures.minecraft.net/texture/abc\",\"variant\":\"SLIM\",\"state\":\"ACTIVE\"}],\"capes\":[{\"id\":\"owned\",\"url\":\"https://textures.minecraft.net/texture/def\",\"alias\":\"Minecon\",\"state\":\"INACTIVE\"}]}"));
    Check(profile.Skins.Single().Active && profile.Skins.Single().Variant == "SLIM");
    Check(profile.Capes.Single().Id == "owned" && !profile.Capes.Single().Active);
    Check(AppearanceService.TextureUri("http://textures.minecraft.net/texture/abc").Scheme == "https");
    return Task.CompletedTask;
});
await Test("Untrusted skin URLs are rejected before network access", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Unexpected HTTP")));
    var service = new AppearanceService(http, NewPaths());
    await Throws<InvalidDataException>(() => service.TextureAsync(new("bad", "https://example.com/texture/test", "CLASSIC", "", false), default));
});
await Test("Capes not owned by the authenticated account cannot be equipped", async () =>
{
    var writes = 0;
    using var http = new HttpClient(new FakeHandler(r =>
    {
        if (r.Method != HttpMethod.Get) writes++;
        return Task.FromResult(Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? licenseJson : profileJson));
    }));
    var account = await LicensedAccount.VerifyAsync(http, "test-token", default);
    await Throws<InvalidOperationException>(() => new AppearanceService(http, NewPaths()).ApplyAsync(account, null, new("not-owned", "https://textures.minecraft.net/texture/abc", "", "", false), default));
    Check(writes == 0);
});
await Test("An owned cape is applied through the authenticated profile API", async () =>
{
    var equipped = false;
    string CapeProfile() => JsonSerializer.Serialize(new { id = "0123456789abcdef0123456789abcdef", name = "LicensedPlayer", skins = Array.Empty<object>(), capes = new[] { new { id = "owned-cape", url = "https://textures.minecraft.net/texture/abc", state = equipped ? "ACTIVE" : "INACTIVE", alias = "Owned" } } });
    using var http = new HttpClient(new FakeHandler(async r =>
    {
        Check(r.Headers.Authorization?.ToString() == "Bearer test-token");
        if (r.Method == HttpMethod.Put)
        {
            Check(r.RequestUri!.AbsolutePath.EndsWith("/capes/active") && (await r.Content!.ReadAsStringAsync()).Contains("owned-cape"));
            equipped = true; return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return Reply(r.RequestUri!.AbsolutePath.Contains("entitlements") ? licenseJson : CapeProfile());
    }));
    var account = await LicensedAccount.VerifyAsync(http, "test-token", default);
    var service = new AppearanceService(http, NewPaths());
    var profile = service.Remember(account);
    var updated = await service.ApplyAsync(account, null, profile.Capes.Single(), default);
    Check(equipped && updated.Capes.Single().Active);
});
await Test("Microsoft redirect_uri error is detected before opening the browser", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Reply("<html><span id=\"errorDescription\">The provided value for the input parameter &#39;redirect_uri&#39; is not valid.</span></html>"))));
    await Throws<OAuthConfigurationException>(() => OAuthRegistration.ValidateAsync(http, new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=test&prompt=select_account"), new Uri("http://localhost:54321/"), default));
});
await Test("Registration probe retains PKCE and callback but requires no account interaction", async () =>
{
    var calls = 0;
    using var http = new HttpClient(new FakeHandler(r =>
    {
        calls++;
        var query = Authentication.ParseQuery(r.RequestUri!.Query);
        Check(query["prompt"] == "none" && query["redirect_uri"] == "http://localhost:54321/");
        Check(query["code_challenge"] == "test-challenge" && query["state"] == "test-state");
        Check(r.Headers.Authorization == null);
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("http://localhost:54321/?error=interaction_required&state=test-state");
        return Task.FromResult(response);
    }));
    await OAuthRegistration.ValidateAsync(http, new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=test&prompt=select_account&redirect_uri=http%3A%2F%2Flocalhost%3A54321%2F&code_challenge=test-challenge&state=test-state"), new Uri("http://localhost:54321/"), default);
    Check(calls == 1, "A local callback must never be followed by the registration probe");
});
await Test("Registration probe follows Microsoft redirects but refuses other hosts", async () =>
{
    var calls = 0;
    using var http = new HttpClient(new FakeHandler(_ =>
    {
        calls++;
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(calls == 1 ? "https://login.live.com/oauth20_authorize.srf" : "https://example.com/not-microsoft");
        return Task.FromResult(response);
    }));
    await Throws<OAuthConfigurationException>(() => OAuthRegistration.ValidateAsync(http, new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=test"), new Uri("http://localhost:54321/"), default));
    Check(calls == 2);
});
await Test("A normal sign-in page is left to the interactive browser", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Reply("<html><title>Sign in to Microsoft</title><form>Account</form></html>"))));
    await OAuthRegistration.ValidateAsync(http, new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=test"), new Uri("http://localhost:54321/"), default);
});
await Test("Microsoft registration outages are reported without response secrets", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Reply("private-server-diagnostic", HttpStatusCode.ServiceUnavailable))));
    try
    {
        await OAuthRegistration.ValidateAsync(http, new Uri("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?client_id=test"), new Uri("http://localhost:54321/"), default);
        throw new Exception("Expected ServiceException");
    }
    catch (ServiceException ex) { Check(!ex.Message.Contains("private-server-diagnostic")); }
});
await Test("Minecraft app approval errors are explicit and do not expose response contents", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Reply("{\"errorMessage\":\"Invalid app registration, private-token-must-not-leak\"}", HttpStatusCode.Forbidden))));
    try
    {
        await Network.JsonAsync(http, HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox", new { identityToken = "secret" }, null, default);
        throw new Exception("Expected MinecraftAppApprovalException");
    }
    catch (MinecraftAppApprovalException ex)
    {
        Check(ex.Message.Contains("https://aka.ms/mce-reviewappid") && !ex.Message.Contains("private-token-must-not-leak"));
    }
});
await Test("Other forbidden responses are not misreported as app approval errors", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Reply("<html>Forbidden</html>", HttpStatusCode.Forbidden))));
    await Throws<ServiceException>(() => Network.JsonAsync(http, HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox", new { identityToken = "secret" }, null, default));
});
await Test("Pending Minecraft approval preserves refresh credentials and recovers without a browser", async () =>
{
    var paths = NewPaths();
    SecretStore.Save(paths.FilePath("session.bin"), new Session { ClientId = clientId, RefreshToken = "old-refresh" });
    var approved = false; var refreshes = 0;
    using var http = new HttpClient(new FakeHandler(async r =>
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/token"))
        {
            var body = await r.Content!.ReadAsStringAsync();
            Check(body.Contains(refreshes++ == 0 ? "old-refresh" : "new-refresh"));
            return Reply("{\"access_token\":\"ms-token\",\"refresh_token\":\"new-refresh\"}");
        }
        if (path is "/user/authenticate" or "/xsts/authorize") return Reply("{\"Token\":\"xbox-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"test-hash\"}]}}");
        if (path == "/authentication/login_with_xbox") return approved
            ? Reply("{\"access_token\":\"minecraft-token\",\"expires_in\":86400}")
            : Reply("{\"errorMessage\":\"Invalid app registration\"}", HttpStatusCode.Forbidden);
        return Reply(path.Contains("entitlements") ? licenseJson : profileJson);
    }));
    var auth = new Authentication(http, paths, clientId);
    await Throws<MinecraftAppApprovalException>(() => auth.AuthenticateAsync(false, progress, default));
    var saved = SecretStore.Load<Session>(paths.FilePath("session.bin"))!;
    Check(saved.RefreshToken == "new-refresh" && saved.MinecraftToken == "");
    approved = true;
    var account = await auth.AuthenticateAsync(false, progress, default);
    Check(account.Name == "LicensedPlayer" && refreshes == 2);
});
await Test("Green particles stay near the top, reach below navigation and have a bounded population", () =>
{
    using var particles = new HeaderParticles();
    const int height = HeaderParticles.EffectTop + HeaderParticles.BandHeight;
    var band = particles.Band(1280, height);
    Check(band.Top == 96 && band.Bottom == 260);
    var reachedBelowHeader = false;
    for (var frame = 0; frame < 1200; frame++)
    {
        particles.Advance(1d / 120, 1280, height);
        Check(particles.ActiveCount <= 128);
        foreach (var bounds in particles.LiveBounds)
        {
            Check(bounds.Top >= band.Top && bounds.Bottom <= band.Bottom && bounds.Left >= 0 && bounds.Right <= 1280);
            reachedBelowHeader |= bounds.Top > TabCarousel.DefaultHeight + 35;
        }
    }
    Check(particles.ActiveCount > 0);
    Check(reachedBelowHeader);
    return Task.CompletedTask;
});
await Test("Particles survive a short fall and disappear after emission stops", () =>
{
    using var particles = new HeaderParticles();
    for (var frame = 0; frame < 60; frame++) particles.Advance(1d / 120, 1280, 260);
    Check(particles.ActiveCount > 0);
    for (var frame = 0; frame < 120; frame++) particles.Advance(1d / 120, 1280, 260, emit: false);
    Check(particles.ActiveCount > 0);
    for (var frame = 0; frame < 510; frame++) particles.Advance(1d / 120, 1280, 260, emit: false);
    Check(particles.ActiveCount == 0);
    return Task.CompletedTask;
});
await Test("Particle renderer clips glow and sprites out of the main content", () =>
{
    using var particles = new HeaderParticles();
    for (var frame = 0; frame < 40; frame++) particles.Advance(1d / 120, 320, 300);
    using var image = new System.Drawing.Bitmap(320, 300);
    using (var g = System.Drawing.Graphics.FromImage(image)) { g.Clear(System.Drawing.Color.Transparent); particles.Draw(g, 320, 300); }
    var drawn = 0;
    for (var y = 0; y < image.Height; y++)
    for (var x = 0; x < image.Width; x++)
    {
        if (image.GetPixel(x, y).A == 0) continue;
        drawn++;
        Check(y >= HeaderParticles.EffectTop && y < HeaderParticles.EffectTop + HeaderParticles.BandHeight);
    }
    Check(drawn > 0);
    return Task.CompletedTask;
});
await Test("Local nickname gets the standard stable offline UUID", () =>
{
    var account = OfflineAccount.Create("Notch");
    Check(account.Name == "Notch" && account.Uuid == "b50ad385829d3141a2167e7d7539ba7f");
    Check(OfflineAccount.Create("  Notch  ").Uuid == account.Uuid);
    Check(OfflineAccount.Create("notch").Uuid != account.Uuid);
    return Task.CompletedTask;
});
await Test("Local names reject invalid characters and unsafe argument input", async () =>
{
    foreach (var name in new[] { "", "ab", new string('a', 17), "Имя", "name with space", "name\"", "abc\nxyz", "../../other" })
        await Throws<InvalidOperationException>(() => Task.Run(() => OfflineAccount.Create(name)));
});
await Test("Local launch arguments use the nickname without any Microsoft requests", async () =>
{
    using var http = new HttpClient(new FakeHandler(_ => throw new Exception("Local launch must not call authentication services")));
    var paths = NewPaths();
    var account = OfflineAccount.Create("Eclipse_Test");
    var plan = new InstallPlan
    {
        Version = "26.3", MainClass = "net.minecraft.client.main.Main", JavaRelativePath = "runtime/bin/javaw.exe",
        JvmArguments = ["-cp", "${classpath}"],
        GameArguments = ["--username", "${auth_player_name}", "--uuid", "${auth_uuid}", "--accessToken", "${auth_access_token}", "--userType", "${user_type}"]
    };
    var info = await new Minecraft(http, paths).CreateOfflineStartInfoAsync(plan, account, 4096, default);
    Check(info.ArgumentList.Contains("Eclipse_Test") && info.ArgumentList.Contains(account.Uuid));
    Check(info.ArgumentList.Contains("legacy") && info.ArgumentList.Contains("0") && !info.ArgumentList.Contains("--demo"));
    Check(!info.UseShellExecute && !info.Environment.ContainsKey("JAVA_TOOL_OPTIONS"));
    var saved = await File.ReadAllTextAsync(info.ArgumentList[0][1..]);
    Check(!saved.Contains("Eclipse_Test") && !saved.Contains(account.Uuid));
});
await Test("UI click cues are short, quiet PCM waves with clean endpoints", () =>
{
    foreach (var cue in Enum.GetValues<UiCue>())
    {
        var wave = UiSounds.CreateWave(cue);
        Check(Encoding.ASCII.GetString(wave, 0, 4) == "RIFF" && Encoding.ASCII.GetString(wave, 8, 8) == "WAVEfmt ");
        Check(BitConverter.ToInt32(wave, 4) == wave.Length - 8 && BitConverter.ToInt32(wave, 40) == wave.Length - 44);
        Check(BitConverter.ToInt16(wave, 20) == 1 && BitConverter.ToInt16(wave, 22) == 1 && BitConverter.ToInt16(wave, 34) == 16);
        Check(BitConverter.ToInt32(wave, 24) == 44100 && (wave.Length - 44) / 88200d is >= .017 and <= .081);
        Check(BitConverter.ToInt16(wave, 44) == 0 && BitConverter.ToInt16(wave, wave.Length - 2) == 0);
        var peak = 0;
        for (var i = 44; i < wave.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wave, i)));
        Check(peak is > 300 and < 6500);
    }
    return Task.CompletedTask;
});
await Test("UI sound preference defaults on for existing settings and retains mute", () =>
{
    Check(JsonSerializer.Deserialize<Settings>("{}")!.InterfaceSounds);
    var saved = JsonSerializer.Serialize(new Settings { InterfaceSounds = false });
    Check(!JsonSerializer.Deserialize<Settings>(saved)!.InterfaceSounds);
    return Task.CompletedTask;
});
await Test("Fabric inherits vanilla, verifies libraries, overrides duplicate artifacts and starts warm without HTTP", () => LoaderTests.InstallAsync("fabric", true, root));
await Test("Quilt resolves published SHA-1 and size and keeps an isolated game directory", () => LoaderTests.InstallAsync("quilt", false, root));
await Test("Loader Maven coordinates handle classifiers and reject external paths", async () =>
{
    Check(ModLoaders.MavenPath("net.fabricmc:sponge-mixin:0.17.5+mixin.0.8.7") == "net/fabricmc/sponge-mixin/0.17.5+mixin.0.8.7/sponge-mixin-0.17.5+mixin.0.8.7.jar");
    Check(ModLoaders.MavenPath("org.example:example:1.0:client").EndsWith("example-1.0-client.jar"));
    foreach (var value in new[] { "org.test:../outside:1.0", "org.test:lib:../../bad", "https://example.com/lib.jar" })
        await Throws<InvalidDataException>(() => Task.Run(() => ModLoaders.MavenPath(value)));
});
await Test("Loader catalogs prefer stable releases and fall back to a local cache", async () =>
{
    var offline = false;
    using var http = new HttpClient(new FakeHandler(_ => offline ? throw new HttpRequestException("offline") : Task.FromResult(Reply("[{\"loader\":{\"version\":\"0.19.0\",\"stable\":true}},{\"loader\":{\"version\":\"0.20.0-beta.1\",\"stable\":false}},{\"loader\":{\"version\":\"0.20.1\",\"stable\":true}}]"))));
    var paths = NewPaths(); var service = new ModLoaders(http, paths, new Minecraft(http, paths));
    var versions = await service.VersionsAsync("fabric", "1.21.1", default);
    Check(versions[0].Version == "0.20.1" && !versions[^1].Stable);
    offline = true;
    Check((await service.VersionsAsync("fabric", "1.21.1", default))[0].Version == "0.20.1");
});
await Test("Reference poses have distinct hand positions and continuous elbow joints", () =>
{
    var raised = new SkinPoseRig(StandingPose.HandAtHead);
    var relaxed = new SkinPoseRig(StandingPose.Relaxed);
    var hand = new Vector3(6, 13, 0);
    Check(Vector3.Transform(hand, raised.Transforms[(int)SkinBone.LeftForearm]).Y > 24);
    Check(Vector3.Transform(hand, relaxed.Transforms[(int)SkinBone.LeftForearm]).Y < 18);
    foreach (var rig in new[] { raised, relaxed })
    {
        var elbow = new Vector3(6, 18, 0);
        Check(Vector3.Distance(Vector3.Transform(elbow, rig.Transforms[(int)SkinBone.LeftUpperArm]), Vector3.Transform(elbow, rig.Transforms[(int)SkinBone.LeftForearm])) < .0001f);
        var knee = new Vector3(-2, 6, 0);
        Check(Vector3.Distance(Vector3.Transform(knee, rig.Transforms[(int)SkinBone.RightThigh]), Vector3.Transform(knee, rig.Transforms[(int)SkinBone.RightShin])) < .0001f);
    }
    return Task.CompletedTask;
});
await Test("Idle stays smooth and grounded across both poses and slim arms", () =>
{
    foreach (var pose in Enum.GetValues<StandingPose>())
    foreach (var width in new[] { 3f, 4f })
    {
        var rig = new SkinPoseRig(pose); rig.SetArmWidth(width);
        var point = new Vector3(0, 32, 0);
        var first = Vector3.Transform(point, rig.Transforms[(int)SkinBone.Head]);
        var previous = first;
        var moved = false;
        for (var frame = 0; frame < 1200; frame++)
        {
            rig.Advance(1d / 120);
            var current = Vector3.Transform(point, rig.Transforms[(int)SkinBone.Head]);
            Check(float.IsFinite(current.X) && float.IsFinite(current.Y) && float.IsFinite(current.Z));
            Check(Vector3.Distance(current, previous) < .05f);
            moved |= Vector3.Distance(current, first) > .08f;
            previous = current;
            var lowest = float.MaxValue;
            foreach (var bone in new[] { SkinBone.RightShin, SkinBone.LeftShin })
            for (var x = -2; x <= 2; x += 4)
            for (var z = -2; z <= 2; z += 4)
                lowest = Math.Min(lowest, Vector3.Transform(new((bone == SkinBone.RightShin ? -2 : 2) + x, 0, z), rig.Transforms[(int)bone]).Y);
            Check(Math.Abs(lowest) < .0001f);
        }
        Check(moved);
    }
    return Task.CompletedTask;
});
await Test("Cape hangs vertically and clears every skin part throughout both idle poses", CapeTests.HangingWithoutClipping);
await Test("Playtime accumulates only active monotonic sessions, checkpoints and restores", () => LauncherFeatureTests.PlaytimeAsync(root));
await Test("Actual game process records playtime; a failed launch records none", LauncherFeatureTests.ActualProcessAsync);
await Test("GitHub update check accepts only newer stable releases without account credentials", LauncherFeatureTests.ReleasesAsync);
await Test("Updates reject foreign repositories, invalid hashes and oversized assets", LauncherFeatureTests.UntrustedAsync);
await Test("GitHub releases without a digest use the published SHA256SUMS file", LauncherFeatureTests.LegacyChecksumAsync);
await Test("Update download verifies SHA-256 and rejects corruption, truncation and cancellation", () => LauncherFeatureTests.DownloadsAsync(root));
await Test("One-click installer arguments preserve app and custom data directories", () => LauncherFeatureTests.InstallerArgumentsAsync(root));
await Test("English is the default for new and upgraded settings; language persists", () => LocalizationTests.SettingsAsync(root, initialLanguage));
await Test("All English resources are translated and preserve formatting placeholders", LocalizationTests.CatalogAsync);
await Test("Errors and hour formatting use the selected language", LocalizationTests.MessagesAsync);
Console.WriteLine($"{passed} tests passed.");
return 0;

sealed class InlineProgress(Action<string>? action = null) : IProgress<string>
{
    public void Report(string value) => action?.Invoke(value);
}
sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
}
