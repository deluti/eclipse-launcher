using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eclipse;

public sealed record LocalSkin(string Id, string Name, string Variant)
{
    internal PlayerTexture Texture(bool active) => new("local:" + Id, "eclipse-local:" + Id, Variant, Name, active);
}

internal sealed class SkinLibrary(AppPaths paths)
{
    private sealed class Index
    {
        public Index() { }
        public string? SelectedId { get; set; }
        public List<LocalSkin> Skins { get; set; } = [];
    }
    private string IndexFile => paths.FilePath("appearance/library.json");
    internal (long Size, long Modified) Revision { get { var file = new FileInfo(IndexFile); return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : (0, 0); } }
    private Index Read() => JsonStore.Read<Index>(IndexFile) ?? new();
    internal IReadOnlyList<LocalSkin> All() => Read().Skins;
    internal LocalSkin? Selected() { var index = Read(); return index.Skins.FirstOrDefault(s => s.Id == index.SelectedId); }
    internal LocalSkin? Find(PlayerTexture? texture)
        => texture?.Url.StartsWith("eclipse-local:", StringComparison.Ordinal) == true ? All().FirstOrDefault(s => texture.Url == "eclipse-local:" + s.Id && texture.Id == "local:" + s.Id) : null;
    internal string FilePath(LocalSkin skin)
    {
        if (!Regex.IsMatch(skin.Id, "\\A[a-f0-9]{64}\\z")) throw new InvalidDataException(L.T("Недопустимый идентификатор скина."));
        return AppPaths.SafeChild(paths.Root, "appearance/local-skins/" + skin.Id + ".png");
    }
    internal LocalSkin Import(string source, bool slim)
    {
        var info = new FileInfo(source);
        if (info.Length > 2 * 1024 * 1024) throw new InvalidDataException(L.T("Текстура слишком большая."));
        var bytes = File.ReadAllBytes(source);
        using var bitmap = Decode(bytes);
        if (slim && bitmap.Height != 64) throw new InvalidDataException(L.T("Для тонких рук нужен скин 64×64."));
        using var normalized = new MemoryStream();
        bitmap.Save(normalized, ImageFormat.Png);
        var png = normalized.ToArray();
        var variant = slim ? "SLIM" : "CLASSIC";
        var id = Convert.ToHexString(SHA256.HashData(png.Concat(System.Text.Encoding.ASCII.GetBytes(variant)).ToArray())).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(source);
        if (name.Length > 48) name = name[..48];
        var skin = new LocalSkin(id, name, variant);
        var target = FilePath(skin);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, png);
        var index = Read();
        if (!index.Skins.Any(s => s.Id == id)) index.Skins.Insert(0, skin);
        JsonStore.Write(IndexFile, index);
        return index.Skins.First(s => s.Id == id);
    }
    internal Bitmap Load(LocalSkin skin) => Decode(File.ReadAllBytes(FilePath(skin)));
    internal byte[] Bytes(LocalSkin skin)
    {
        var bytes = File.ReadAllBytes(FilePath(skin));
        using var verified = Decode(bytes);
        return bytes;
    }
    internal void Select(LocalSkin? skin)
    {
        var index = Read();
        if (skin != null && !index.Skins.Any(s => s == skin)) throw new InvalidOperationException(L.T("Скин отсутствует в библиотеке."));
        index.SelectedId = skin?.Id;
        JsonStore.Write(IndexFile, index);
    }
    internal static Bitmap Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || bytes.Length > 2 * 1024 * 1024 || !bytes.AsSpan(0, 8).SequenceEqual(signature)
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != 64
            || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) is not (32 or 64))
            throw new InvalidDataException(L.T("Выберите PNG-скин размером 64×64 или 64×32."));
        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream, false, true);
            return new Bitmap(image);
        }
        catch (ArgumentException ex) { throw new InvalidDataException(L.T("Не удалось прочитать PNG-скин."), ex); }
    }
}
