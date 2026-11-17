using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eclipse;

public sealed class Session
{
    public string ClientId { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string MinecraftToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string Name { get; set; } = "";
    public string Xuid { get; set; } = "";
}

// Microsoft launches require a verified account; local nickname profiles use OfflineAccount.
public sealed class LicensedAccount
{
    public string Name { get; }
    public string Uuid { get; }
    internal string AccessToken { get; }
    internal string ClientId { get; }
    internal string Xuid { get; }
    internal JsonElement Profile { get; }
    public DateTimeOffset VerifiedAt { get; } = DateTimeOffset.UtcNow;
    private LicensedAccount(string name, string uuid, string token, string clientId, string xuid, JsonElement profile)
    { Name = name; Uuid = uuid; AccessToken = token; ClientId = clientId; Xuid = xuid; Profile = profile; }

    internal static async Task<LicensedAccount> VerifyAsync(HttpClient http, string token, CancellationToken ct, string clientId = "", string xuid = "")
    {
        var entitlementTask = Network.JsonAsync(http, HttpMethod.Get,
            "https://api.minecraftservices.com/entitlements/license?requestId=" + Guid.NewGuid(), null, token, ct);
        var profileTask = Network.JsonAsync(http, HttpMethod.Get,
            "https://api.minecraftservices.com/minecraft/profile", null, token, ct);
        await Task.WhenAll(entitlementTask, profileTask).ConfigureAwait(false);
        var entitlements = await entitlementTask.ConfigureAwait(false);
        if (!HasLicense(entitlements))
            throw new InvalidOperationException("На этом аккаунте нет действующей лицензии Minecraft Java Edition. Запуск запрещён.");
        var profile = await profileTask.ConfigureAwait(false);
        var name = profile.Text("name");
        var uuid = profile.Text("id");
        if (!Regex.IsMatch(uuid, "\\A[0-9a-fA-F]{32}\\z") || !Regex.IsMatch(name, "\\A[A-Za-z0-9_]{1,16}\\z"))
            throw new InvalidDataException("Minecraft вернул некорректный игровой профиль.");
        return new LicensedAccount(name, uuid, token, clientId, xuid, profile);
    }

    internal static bool HasLicense(JsonElement json) => json.TryGetProperty("items", out var items)
        && items.ValueKind == JsonValueKind.Array
        && items.EnumerateArray().Any(item => item.TryGetProperty("name", out var name)
            && name.GetString() is "game_minecraft" or "product_minecraft");
}

public sealed class Authentication(HttpClient http, AppPaths paths, string clientId)
{
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private const string Scope = "XboxLive.signin offline_access";
    private string SessionPath => paths.FilePath("session.bin");
    public string? SavedName
    {
        get { try { return SecretStore.Load<Session>(SessionPath)?.Name; } catch (CryptographicException) { return null; } }
    }
    public void SignOut() { if (File.Exists(SessionPath)) File.Delete(SessionPath); }
    public bool IsConfigured => Guid.TryParse(clientId, out var id) && id != Guid.Empty;

    public async Task<LicensedAccount> AuthenticateAsync(bool interactive, IProgress<string> progress, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Разработчик должен указать собственный MicrosoftClientId в launcher-config.json и получить доступ к Minecraft API. После настройки игрок входит в свой аккаунт Microsoft кнопкой «Войти». Пароль вводится только на сайте Microsoft.");
        Session? session = null;
        try { session = SecretStore.Load<Session>(SessionPath); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { SignOut(); }
        if (session?.ClientId != clientId) session = null;

        if (!interactive && session != null && session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            progress.Report("Проверка лицензии Minecraft…");
            try { return await VerifyAndSaveAsync(session, ct).ConfigureAwait(false); }
            catch (ServiceException ex) when (ex.Status == HttpStatusCode.Unauthorized) { /* Refresh once. */ }
        }

        JsonElement microsoft;
        if (!interactive && !string.IsNullOrEmpty(session?.RefreshToken))
        {
            progress.Report("Обновление входа Microsoft…");
            try
            {
                microsoft = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken }, ct).ConfigureAwait(false);
            }
            catch (ServiceException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                SignOut();
                throw new InvalidOperationException("Срок входа Microsoft истёк. Нажмите «Войти через Microsoft».");
            }
        }
        else
        {
            progress.Report("Завершите вход в браузере Microsoft…");
            microsoft = await BrowserLoginAsync(ct).ConfigureAwait(false);
        }

        session = new Session
        {
            ClientId = clientId,
            RefreshToken = microsoft.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? "" : session?.RefreshToken ?? ""
        };
        // Persist rotated refresh credentials even if the downstream Xbox service is temporarily unavailable.
        SecretStore.Save(SessionPath, session);
        progress.Report("Вход Xbox Live…");
        var xbox = await Network.JsonAsync(http, HttpMethod.Post, "https://user.auth.xboxlive.com/user/authenticate",
            new { Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoft.Text("access_token") }, RelyingParty = "http://auth.xboxlive.com", TokenType = "JWT" }, null, ct).ConfigureAwait(false);
        var xsts = await Network.JsonAsync(http, HttpMethod.Post, "https://xsts.auth.xboxlive.com/xsts/authorize",
            new { Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xbox.Text("Token") } }, RelyingParty = "rp://api.minecraftservices.com/", TokenType = "JWT" }, null, ct).ConfigureAwait(false);
        var hash = xsts.GetProperty("DisplayClaims").GetProperty("xui")[0].Text("uhs");
        var claims = xsts.GetProperty("DisplayClaims").GetProperty("xui")[0];
        if (claims.TryGetProperty("xid", out var xid)) session.Xuid = xid.GetString() ?? "";
        if (hash != xbox.GetProperty("DisplayClaims").GetProperty("xui")[0].Text("uhs"))
            throw new InvalidDataException("Xbox вернул несовпадающие идентификаторы аккаунта.");
        progress.Report("Получение сессии Minecraft…");
        var minecraft = await Network.JsonAsync(http, HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={hash};{xsts.Text("Token")}" }, null, ct).ConfigureAwait(false);
        session.MinecraftToken = minecraft.Text("access_token");
        session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(minecraft.GetProperty("expires_in").GetInt32());
        progress.Report("Проверка лицензии Minecraft…");
        return await VerifyAndSaveAsync(session, ct).ConfigureAwait(false);
    }

    private async Task<LicensedAccount> VerifyAndSaveAsync(Session session, CancellationToken ct)
    {
        var account = await LicensedAccount.VerifyAsync(http, session.MinecraftToken, ct, session.ClientId, session.Xuid).ConfigureAwait(false);
        session.Name = account.Name;
        SecretStore.Save(SessionPath, session);
        return account;
    }

    private Task<JsonElement> TokenAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        values["client_id"] = clientId;
        values["scope"] = Scope;
        return Network.JsonAsync(http, HttpMethod.Post, Authority + "token", values, null, ct, form: true);
    }

    internal static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private async Task<JsonElement> BrowserLoginAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        // The listener owns the ephemeral port until authentication completes; no port reservation race.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://localhost:{port}/";
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = Scope, ["state"] = state, ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256", ["prompt"] = "select_account"
        };
        var url = Authority + "authorize?" + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        using (var registration = OAuthRegistration.CreateClient())
            await OAuthRegistration.ValidateAsync(registration, new Uri(url), new Uri(redirect), ct).ConfigureAwait(false);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            using var stream = connection.GetStream();
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(requestDeadline.Token).ConfigureAwait(false);
            if (line == null || line.Length > 16384 || !line.StartsWith("GET /?", StringComparison.Ordinal)) continue;
            var target = line.Split(' ')[1];
            var parameters = ParseQuery(new Uri("http://localhost" + target).Query);
            if (!parameters.TryGetValue("state", out var returnedState)
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returnedState))) continue;
            var body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Eclipse</title>Возвращайтесь в Eclipse. Это окно можно закрыть.");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\n\r\n");
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, deadline.Token).ConfigureAwait(false);
            if (parameters.ContainsKey("error")) throw new InvalidOperationException("Вход Microsoft отменён или не разрешён.");
            if (!parameters.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                throw new InvalidDataException("Microsoft не вернул код входа.");
            return await TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = redirect, ["code_verifier"] = verifier }, deadline.Token).ConfigureAwait(false);
        }
    }

    internal static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]),
            p => p.Length == 2 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "", StringComparer.Ordinal);
}
