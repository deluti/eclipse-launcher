using System.Drawing.Imaging;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Eclipse;

internal static class PublishedStartupFixtures
{
    internal static void Create(string directory)
    {
        foreach (var online in new[] { false, true })
        {
            var paths = new AppPaths(Path.Combine(directory, online ? "cached-microsoft" : "local"));
            JsonStore.Write(paths.FilePath("settings.json"), new Settings { OfflineMode = !online, Version = "26.3", InterfaceSounds = true });
            JsonStore.Write(paths.FilePath("versions.json"), new { latest = new { release = "26.3" }, versions = new[] { new { id = "26.3", type = "release" } } });
            if (!online) continue;
            // Synthetic identity only. Never read or copy a player's real session or tokens.
            SecretStore.Save(paths.FilePath("session.bin"), new Session { Name = "Startup_Test" });
            const string skinUrl = "https://textures.minecraft.net/texture/startup-fixture-skin";
            const string capeUrl = "https://textures.minecraft.net/texture/startup-fixture-cape";
            JsonStore.Write(paths.FilePath("appearance/profile.json"), new AppearanceProfile
            {
                Uuid = new string('a', 32), Name = "Startup_Test",
                Skins = [new("fixture-skin", skinUrl, "SLIM", "", true)],
                Capes = [new("fixture-cape", capeUrl, "", "", true)]
            });
            Directory.CreateDirectory(paths.FilePath("appearance/textures"));
            string Cached(string url) => paths.FilePath("appearance/textures/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".png");
            using var skin = SkinScene.CreateDefaultSkin(); skin.Save(Cached(skinUrl), ImageFormat.Png);
            using var cape = new Bitmap(64, 32);
            using (var graphics = Graphics.FromImage(cape)) graphics.Clear(Color.ForestGreen);
            cape.Save(Cached(capeUrl), ImageFormat.Png);
        }
    }
    internal static void VerifyAssembly(string path)
    {
        using var file = File.OpenRead(path); using var pe = new PEReader(file);
        if (pe.PEHeaders.CorHeader == null || pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size != 0)
            throw new InvalidDataException("The launcher assembly must use JIT compilation, not ReadyToRun.");
    }
}
