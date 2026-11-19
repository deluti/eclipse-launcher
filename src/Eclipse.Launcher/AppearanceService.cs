using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eclipse;

public sealed record PlayerTexture(string Id, string Url, string Variant, string Alias, bool Active);
public sealed class AppearanceProfile
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PlayerTexture> Skins { get; set; } = [];
    public List<PlayerTexture> Capes { get; set; } = [];
    public static AppearanceProfile Parse(JsonElement json)
    {
        string Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        List<PlayerTexture> Textures(string key) => !json.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array ? [] : array.EnumerateArray()
            .Select(item => new PlayerTexture(Text(item, "id"), Text(item, "url"), Text(item, "variant"), Text(item, "alias"), Text(item, "state") == "ACTIVE")).ToList();
        return new() { Uuid = Text(json, "id"), Name = Text(json, "name"), Skins = Textures("skins"), Capes = Textures("capes") };
    }
}

public sealed class AppearanceService(HttpClient http, AppPaths paths)
{
    internal SkinLibrary Library { get; } = new(paths);
    private const string ProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    public AppearanceProfile? CachedProfile()
    {
        try { return JsonStore.Read<AppearanceProfile>(paths.FilePath("appearance/profile.json")); }
        catch (JsonException) { return null; }
    }
    public AppearanceProfile Remember(LicensedAccount account)
    {
        var profile = AppearanceProfile.Parse(account.Profile);
        SaveProfile(profile);
        return profile;
    }
    private void SaveProfile(AppearanceProfile profile)
    {
        // Keep the player's previously observed skins. Minecraft's profile API is not a complete skin-history API.
        var wardrobePath = AppPaths.SafeChild(paths.Root, "appearance/players/" + profile.Uuid + ".json");
        AppearanceProfile? old = null;
        try { old = JsonStore.Read<AppearanceProfile>(wardrobePath); } catch (JsonException) { }
        if (old != null && old.Uuid == profile.Uuid)
            foreach (var item in old.Skins)
                if (!profile.Skins.Any(s => s.Url == item.Url)) profile.Skins.Add(item with { Active = false });
        JsonStore.Write(wardrobePath, profile);
        JsonStore.Write(paths.FilePath("appearance/profile.json"), profile);
    }
    internal static Uri TextureUri(string url)
    {
        var uri = new Uri(url);
        if (uri.Host != "textures.minecraft.net" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Scheme is not ("http" or "https") || !uri.AbsolutePath.StartsWith("/texture/", StringComparison.Ordinal))
            throw new InvalidDataException(L.T("Неизвестный источник текстуры игрока."));
        return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    }
    public async Task<Bitmap> TextureAsync(PlayerTexture texture, CancellationToken ct)
    {
        if (texture.Url.StartsWith("eclipse-local:", StringComparison.Ordinal))
            return Library.Load(Library.Find(texture) ?? throw new InvalidDataException(L.T("Скин отсутствует в библиотеке.")));
        var uri = TextureUri(texture.Url);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        var cache = paths.FilePath("appearance/textures/" + key + ".png");
        byte[] bytes;
        if (File.Exists(cache)) bytes = await File.ReadAllBytesAsync(cache, ct).ConfigureAwait(false);
        else
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException(L.T("Текстура слишком большая."));
                output.Write(buffer, 0, count);
            }
            bytes = output.ToArray();
        }
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        if (image.Width != 64 || image.Height is not (32 or 64)) throw new InvalidDataException(L.T("Неверный размер текстуры игрока."));
        var bitmap = new Bitmap(image);
        if (!File.Exists(cache))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            await File.WriteAllBytesAsync(cache, bytes, ct).ConfigureAwait(false);
        }
        return bitmap;
    }
    public async Task<AppearanceProfile> ApplyAsync(LicensedAccount account, PlayerTexture? skin, PlayerTexture? cape, CancellationToken ct)
    {
        var profile = Remember(account);
        var local = Library.Find(skin);
        if (skin != null && local == null && !profile.Skins.Any(item => item.Url == skin.Url)) throw new InvalidOperationException(L.T("Скин отсутствует в гардеробе игрока."));
        if (cape != null && !profile.Capes.Any(item => item.Id == cape.Id)) throw new InvalidOperationException(L.T("Этот плащ не принадлежит игроку."));
        if (skin != null && !profile.Skins.Any(item => item.Url == skin.Url && item.Active))
        {
            byte[] bytes;
            if (local != null) bytes = Library.Bytes(local);
            else
            {
                using var bitmap = await TextureAsync(skin, ct).ConfigureAwait(false);
                using var png = new MemoryStream(); bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png); bytes = png.ToArray();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, ProfileUrl + "/skins");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(skin.Variant.Equals("SLIM", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic"), "variant");
            var image = new ByteArrayContent(bytes); image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(image, "file", "skin.png"); request.Content = form;
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ServiceException(L.T("Не удалось изменить внешний вид игрока. HTTP ") + (int)response.StatusCode, response.StatusCode);
        }
        var activeCape = profile.Capes.FirstOrDefault(item => item.Active);
        if (activeCape?.Id != cape?.Id)
            await MutateAsync(cape == null ? HttpMethod.Delete : HttpMethod.Put, ProfileUrl + "/capes/active", cape == null ? null : new { capeId = cape.Id }, account.AccessToken, ct).ConfigureAwait(false);
        var json = await Network.JsonAsync(http, HttpMethod.Get, ProfileUrl, null, account.AccessToken, ct).ConfigureAwait(false);
        var updated = AppearanceProfile.Parse(json);
        if (updated.Uuid != account.Uuid) throw new InvalidDataException(L.T("Профиль игрока изменился во время запроса."));
        SaveProfile(updated);
        return updated;
    }
    private async Task MutateAsync(HttpMethod method, string url, object? payload, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload != null) request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ServiceException(L.T("Не удалось изменить внешний вид игрока. HTTP ") + (int)response.StatusCode, response.StatusCode);
    }
}
