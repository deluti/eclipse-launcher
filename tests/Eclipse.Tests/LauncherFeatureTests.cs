using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eclipse;

internal static class LauncherFeatureTests
{
    internal static Task PlaytimeAsync(string root)
    {
        var paths = Paths(root); var clock = new Clock(); var counter = new PlaytimeCounter(paths, clock);
        clock.Advance(600);
        Check(counter.TotalHours == 0, "Launcher idle must not count");
        var first = counter.BeginSession();
        clock.Advance(450);
        clock.Utc = clock.Utc.AddDays(-100);
        Check(Math.Abs(counter.TotalHours - .125) < .00001, "Elapsed time must use a monotonic clock");
        counter.Save(); counter.Save();
        Check(Math.Abs(new PlaytimeCounter(paths).TotalHours - .125) < .00001, "Checkpoint survives restart without double-counting");
        first.Dispose(); first.Dispose(); clock.Advance(900);
        Check(Math.Abs(counter.TotalHours - .125) < .00001, "Closed game stops counting");
        using (counter.BeginSession()) clock.Advance(180);
        Check(Math.Abs(new PlaytimeCounter(paths).TotalHours - .175) < .00001, "Sessions must accumulate");
        Check(PlaytimeCounter.Format(1.26) == "1,3 ч" && PlaytimeCounter.Format(0) == "0,0 ч", "One decimal and Russian separator");
        return Task.CompletedTask;
    }
    internal static async Task ActualProcessAsync()
    {
        var paths = new AppPaths(AppContext.BaseDirectory);
        using var http = new HttpClient(new Handler(_ => throw new Exception("No network expected")));
        var minecraft = new Minecraft(http, paths);
        var before = minecraft.Playtime.TotalHours;
        var plan = new InstallPlan { Version = "playtime-fixture-" + Guid.NewGuid().ToString("N"), MainClass = "fixture", JavaRelativePath = "Eclipse.Tests.exe", GameArguments = ["--eclipse-playtime-fixture"] };
        var exit = await minecraft.LaunchOfflineAsync(plan, OfflineAccount.Create("Time_Test"), 1024, new Progress<string>(), default);
        Check(exit == 0, "Process fixture must exit normally");
        Check(minecraft.Playtime.TotalHours - before >= .2 / 3600, "Track the actual child process lifetime");
        var after = minecraft.Playtime.TotalHours;
        plan.JavaRelativePath = "missing-java.exe";
        try { await minecraft.LaunchOfflineAsync(plan, OfflineAccount.Create("Time_Test"), 1024, new Progress<string>(), default); throw new Exception("Expected process error"); }
        catch (System.ComponentModel.Win32Exception) { }
        Check(minecraft.Playtime.TotalHours == after, "Failed launch must not count preparation time");
        Check(Math.Abs(new PlaytimeCounter(paths).TotalHours - after) < .000001, "Process exit persists elapsed time");
    }
    internal static async Task ReleasesAsync()
    {
        foreach (var (tag, draft, prerelease, expected) in new[] {
            ("v0.2.1", false, false, true), ("v0.2.0", false, false, false), ("v0.1.0", false, false, false),
            ("v0.3.0", true, false, false), ("v0.3.0", false, true, false), ("v0.3.0-beta", false, false, false) })
        {
            using var http = new HttpClient(new Handler(request =>
            {
                Check(request.Headers.Authorization == null && request.Headers.UserAgent.Count > 0, "Public GitHub check sends no account credentials");
                return Reply(Metadata(tag, draft, prerelease));
            }));
            var result = await new LauncherUpdates(http).CheckAsync(default, new Version(0, 2, 0, 0));
            Check((result != null) == expected, "Stable version comparison: " + tag);
        }
        using var missing = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Check(await new LauncherUpdates(missing).CheckAsync(default) == null, "No release yet is harmless");
    }
    internal static async Task UntrustedAsync()
    {
        foreach (var bad in new[] {
            Metadata("v0.3.0").Replace("https://github.com/", "https://evil.example/"),
            Metadata("v0.3.0").Replace("deluti/eclipse-launcher/releases/download", "another/repository/releases/download"),
            Metadata("v0.3.0").Replace(new string('a', 64), "invalid"),
            Metadata("v0.3.0").Replace("\"size\":10", "\"size\":9999999999") })
        {
            using var http = new HttpClient(new Handler(_ => Reply(bad)));
            await ThrowsAsync<InvalidDataException>(() => new LauncherUpdates(http).CheckAsync(default));
        }
    }
    internal static async Task LegacyChecksumAsync()
    {
        var requests = 0;
        var metadata = Metadata("v0.3.0").Replace("\"digest\":\"sha256:" + new string('a', 64) + "\",", "");
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            return Reply(request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt") ? new string('b', 64) + "  " + LauncherUpdates.InstallerName + "\n" : metadata);
        }));
        var release = await new LauncherUpdates(http).CheckAsync(default);
        Check(requests == 2 && release?.Sha256 == new string('b', 64), "Older GitHub releases can supply SHA256SUMS.txt");
    }
    internal static async Task DownloadsAsync(string root)
    {
        byte[] bytes = [0x4d, 0x5a, 1, 2, 3, 4, 5];
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var service = new LauncherUpdates(http);
        var release = new LauncherRelease(new Version(0, 3, 0), "v0.3.0", new Uri(AssetUrl("v0.3.0", LauncherUpdates.InstallerName)), bytes.Length, hash);
        var directory = Paths(root).FilePath("update");
        var progress = new Percent();
        var file = await service.DownloadAsync(release, directory, progress, default);
        Check(File.ReadAllBytes(file).SequenceEqual(bytes) && progress.Value == 100 && !File.Exists(file + ".part"), "Verified update is installed atomically");
        var badDirectory = Paths(root).FilePath("bad-update");
        await ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { Sha256 = new string('0', 64) }, badDirectory, progress, default));
        Check(!Directory.EnumerateFiles(badDirectory).Any(), "Damaged installer and partial download must not survive");
        var truncatedDirectory = Paths(root).FilePath("truncated");
        await ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release with { Size = bytes.Length + 1 }, truncatedDirectory, progress, default));
        Check(!Directory.EnumerateFiles(truncatedDirectory).Any(), "Truncated installer cannot be executed");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => service.DownloadAsync(release, Paths(root).FilePath("cancelled"), progress, cancel.Token));
    }
    internal static Task InstallerArgumentsAsync(string root)
    {
        var app = Path.Combine(root, "Previous Eclipse с пробелами");
        var data = Path.Combine(root, "Сохранённые миры");
        var info = LauncherUpdates.InstallerStartInfo(Path.Combine(root, "update.exe"), app, data, 12345);
        Check(info.UseShellExecute && info.ArgumentList.Contains("/DIR=" + Path.GetFullPath(app)), "Update targets existing app folder");
        Check(info.ArgumentList.Contains("/ECLIPSEDATA=" + Path.GetFullPath(data)) && info.ArgumentList.Contains("/LAUNCHERPID=12345"), "Update retains custom data path and waits for parent");
        Check(info.ArgumentList.Contains("/ECLIPSEUPDATE=1") && info.ArgumentList.Contains("/NORESTART"), "Installer restarts launcher rather than Windows");
        return Task.CompletedTask;
    }
    private static AppPaths Paths(string root) => new(Path.Combine(root, "launcher-features", Guid.NewGuid().ToString("N")));
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        internal DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
        internal void Advance(double seconds) { timestamp += (long)(seconds * TimestampFrequency); Utc = Utc.AddSeconds(seconds); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(reply(request)); }
    }
    private sealed class Percent : IProgress<int> { internal int Value; public void Report(int value) => Value = value; }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    internal static string AssetUrl(string tag, string name) => "https://github.com/deluti/eclipse-launcher/releases/download/" + tag + "/" + name;
    internal static string Metadata(string tag, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new {
        tag_name = tag, draft, prerelease, assets = new object[] {
            new { name = LauncherUpdates.InstallerName, browser_download_url = AssetUrl(tag, LauncherUpdates.InstallerName), digest = "sha256:" + new string('a', 64), size = 10 },
            new { name = "SHA256SUMS.txt", browser_download_url = AssetUrl(tag, "SHA256SUMS.txt"), size = 99 } } });
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
