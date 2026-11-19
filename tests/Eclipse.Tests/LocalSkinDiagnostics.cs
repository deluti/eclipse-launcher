using System.Diagnostics;
using System.Text;
using Eclipse;

internal static class LocalSkinDiagnostics
{
    internal static async Task<int> RunAsync(string gameRoot, string checkRoot, string agent)
    {
        var paths = new AppPaths(checkRoot); var library = new SkinLibrary(paths);
        library.Select(library.Import(SkinProfileTests.Fixture(paths), true));
        Directory.CreateDirectory(paths.FilePath("support")); File.Copy(agent, paths.FilePath("support/authlib-injector-1.2.8.jar"), true);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        await using var runtime = await LocalSkinRuntime.StartAsync(http, paths, OfflineAccount.Create("Eclipse_Test"), new InlineProgress(), default) ?? throw new Exception();
        var plan = JsonStore.Read<InstallPlan>(Path.Combine(gameRoot, "plans/26.3.json"))!;
        var classpath = string.Join(Path.PathSeparator, plan.ClassPath.Select(f => Path.Combine(gameRoot, f)));
        var source = paths.FilePath("SkinProbe.java");
        await File.WriteAllTextAsync(source, """
            import com.mojang.authlib.services.MinecraftServicesDiscoveryService;
            import java.net.*;
            import java.util.*;
            public class SkinProbe {
                public static void main(String[] args) throws Exception {
                    try {
                        var discovery = MinecraftServicesDiscoveryService.create(Proxy.NO_PROXY);
                        var session = discovery.createMinecraftSessionService();
                        var profile = session.fetchProfile(UUID.fromString(args[0]), true);
                        if (profile == null) throw new AssertionError("No profile");
                        var textures = session.unpackTextures(session.getPackedTextures(profile.profile()));
                        for (int attempt = 0; attempt < 40 && textures.signatureState() != com.mojang.authlib.SignatureState.SIGNED; attempt++) {
                            Thread.sleep(100); textures = session.unpackTextures(session.getPackedTextures(profile.profile()));
                        }
                        var skin = textures.skin();
                        if (textures.signatureState() != com.mojang.authlib.SignatureState.SIGNED || skin == null || !skin.getUrl().startsWith(args[1]) || !"slim".equals(skin.getMetadata("model")))
                            throw new AssertionError("Selected slim skin not accepted: " + textures);
                        try (var stream = URI.create(skin.getUrl()).toURL().openStream()) {
                            if (stream.readAllBytes().length < 100) throw new AssertionError("Invalid PNG");
                        }
                        System.out.println("PASS real Mojang authlib: selected slim texture, signature " + textures.signatureState());
                        System.exit(0);
                    } catch (Throwable error) { error.printStackTrace(); System.exit(1); }
                }
            }
            """, new UTF8Encoding(false));
        async Task<int> Run(string exe, IEnumerable<string> arguments, string log)
        {
            var info = new ProcessStartInfo(exe) { WorkingDirectory = paths.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var value in arguments) info.ArgumentList.Add(value);
            foreach (var name in new[] { "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS" }) info.Environment.Remove(name);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55)); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            var text = await output + await errors; await File.WriteAllTextAsync(paths.FilePath(log), text); Console.WriteLine(text);
            return process.ExitCode;
        }
        var bin = Path.GetDirectoryName(Path.Combine(gameRoot, plan.JavaRelativePath))!;
        if (await Run(Path.Combine(bin, "javac.exe"), ["-cp", classpath, source], "compile.log") != 0) return 1;
        var uuid = Guid.ParseExact(OfflineAccount.Create("Eclipse_Test").Uuid, "N").ToString();
        var result = await Run(Path.Combine(bin, "java.exe"), [..runtime.JvmArguments, "-cp", paths.Root + Path.PathSeparator + classpath, "SkinProbe", uuid, runtime.Root], "probe.log");
        JsonStore.Write(paths.FilePath("result.json"), new { exitCode = result, runtime.TextureRequests });
        return result == 0 && runtime.TextureRequests > 0 ? 0 : 1;
    }
}
