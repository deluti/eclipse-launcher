using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Eclipse;

public sealed class ServiceException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

internal sealed class MinecraftAppApprovalException() : InvalidOperationException(
    "Microsoft и Xbox Live приняли вход, но Minecraft отклонил Client ID приложения: Invalid app registration.\n\n" +
    "Владельцу Eclipse нужно получить разрешение Minecraft Java Game Service API для своего Client ID.\n\n" +
    "Страница запроса: https://aka.ms/mce-reviewappid\n" +
    "Справка сервиса: https://aka.ms/AppRegInfo\n\n" +
    "Регистрация в Entra и лицензия игрока не заменяют разрешение для приложения. После одобрения можно повторить запуск.\n" +
    "Данные для запроса подготовлены в MINECRAFT-API-REQUEST.md в папке проекта.");

public static class Network
{
    public static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 12,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EclipseLauncher/0.1");
        return client;
    }

    public static async Task<JsonElement> JsonAsync(HttpClient client, HttpMethod method, string url,
        object? body, string? bearer, CancellationToken ct, bool form = false)
    {
        using var request = new HttpRequestMessage(method, url);
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body != null) request.Content = form
            ? new FormUrlEncodedContent((Dictionary<string, string>)body)
            // Xbox's wire contract uses case-sensitive PascalCase; web defaults silently camel-case it.
            : JsonContent.Create(body, options: JsonSerializerOptions.Default);
        if (url.Contains("xboxlive.com/", StringComparison.Ordinal)) request.Headers.Add("x-xbl-contract-version", "1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Never propagate raw authentication response bodies or tokens into UI/logs.
            var host = new Uri(url).Host;
            if (host == "api.minecraftservices.com" && response.StatusCode == HttpStatusCode.Forbidden)
            {
                var errorBytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                if (IsUnapprovedMinecraftApp(errorBytes)) throw new MinecraftAppApprovalException();
            }
            var message = $"Сервис {host} вернул HTTP {(int)response.StatusCode}.";
            if (url.Contains("xsts.auth"))
                message += " Проверьте наличие профиля Xbox и разрешения семейной группы Microsoft.";
            throw new ServiceException(message, response.StatusCode);
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    internal static bool IsUnapprovedMinecraftApp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 32768) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var key in new[] { "errorMessage", "developerMessage", "error", "message" })
                if (document.RootElement.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String
                    && text.GetString()!.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (JsonException) { }
        return false;
    }
}

public static class JsonExtensions
{
    public static string Text(this JsonElement value, string key) => value.GetProperty(key).GetString()
        ?? throw new InvalidDataException($"Отсутствует значение {key}.");
}
