using System.Net;
using Eclipse;

internal static class AccountDiagnostics
{
    public static async Task<int> VerifyAsync(string configurationPath, string dataDirectory)
    {
        var config = JsonStore.Read<LauncherConfig>(configurationPath) ?? throw new InvalidOperationException("Configuration not found");
        var paths = new AppPaths(dataDirectory);
        var saved = SecretStore.Load<Session>(paths.FilePath("session.bin"));
        if (saved?.ClientId != config.MicrosoftClientId || string.IsNullOrEmpty(saved.RefreshToken))
        {
            Console.Error.WriteLine("No matching saved Microsoft session. Interactive sign-in is required.");
            return 2;
        }
        using var handler = new StatusTrace { InnerHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) } };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EclipseLauncher/0.1");
        try
        {
            var account = await new Authentication(http, paths, config.MicrosoftClientId).AuthenticateAsync(false, new Stages(), default);
            new AppearanceService(http, paths).Remember(account);
            Console.WriteLine("PASS real Microsoft -> Xbox Live -> XSTS -> Minecraft -> license/profile verification. No game was started.");
            return 0;
        }
        catch (Exception ex)
        {
            // Never dump request/response objects, credentials or profile data.
            Console.Error.WriteLine("Account verification stopped: " + ex.Message);
            return 1;
        }
    }
    private sealed class Stages : IProgress<string>
    {
        public void Report(string message) => Console.WriteLine(message);
    }
    private sealed class StatusTrace : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            Console.WriteLine($"{request.RequestUri!.Host}{request.RequestUri.AbsolutePath}: HTTP {(int)response.StatusCode}");
            return response;
        }
    }
}
