using System.Net;
using System.Text.RegularExpressions;

namespace Eclipse;

internal sealed class OAuthConfigurationException(string message) : InvalidOperationException(message);

internal static class OAuthRegistration
{
    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = TimeSpan.FromSeconds(20) };

    // Anonymous requests contain no account cookies, password or tokens.
    // If Microsoft returns a normal login page, the browser proceeds with interactive sign-in.
    internal static async Task ValidateAsync(HttpClient http, Uri authorizationUri, Uri callback, CancellationToken ct)
    {
        var query = Authentication.ParseQuery(authorizationUri.Query);
        query["prompt"] = "none";
        var current = new UriBuilder(authorizationUri) { Query = string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))) }.Uri;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        for (var step = 0; step < 8; step++)
        {
            if (!IsMicrosoft(current)) throw new OAuthConfigurationException("Microsoft вернул неизвестный адрес входа. Проверьте регистрацию приложения.");
            using var response = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme == callback.Scheme && next.Host == callback.Host && next.Port == callback.Port && next.AbsolutePath == callback.AbsolutePath) return;
                current = next;
                continue;
            }
            var content = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            var errorSpan = Regex.Match(content, "<[^>]+\\bid\\s*=\\s*[\"']errorDescription[\"'][^>]*>(.*?)</[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (errorSpan.Success)
            {
                var message = WebUtility.HtmlDecode(Regex.Replace(errorSpan.Groups[1].Value, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
                if (message.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase) || message.Contains("client_id", StringComparison.OrdinalIgnoreCase)) throw ConfigurationError(callback);
            }
            if (content.Contains("AADSTS50011", StringComparison.Ordinal) || content.Contains("AADSTS700016", StringComparison.Ordinal)) throw ConfigurationError(callback);
            if (!response.IsSuccessStatusCode) throw new ServiceException("Сервис входа Microsoft временно недоступен. Повторите попытку.", response.StatusCode);
            return;
        }
        throw new OAuthConfigurationException("Слишком много перенаправлений Microsoft. Проверьте регистрацию приложения.");
    }

    internal static bool IsMicrosoft(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host is "login.microsoftonline.com" or "login.live.com" or "login.microsoft.com" or "account.live.com";

    private static OAuthConfigurationException ConfigurationError(Uri callback) => new(
        "В регистрации Microsoft не настроен адрес возврата для Eclipse.\n\n" +
        "Microsoft Entra → Регистрации приложений → ваше приложение → Проверка подлинности:\n" +
        "1. Добавьте платформу «Мобильные и классические приложения».\n" +
        "2. Добавьте URI http://localhost (http, без порта).\n" +
        "3. Разрешите общедоступные клиентские потоки и сохраните.\n\n" +
        "После сохранения повторите вход.\nАдрес текущей попытки: " + callback.AbsoluteUri);

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > 2 * 1024 * 1024) return "";
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
}
