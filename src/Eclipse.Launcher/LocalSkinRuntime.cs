using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eclipse;

// A game-session-only texture provider. It implements no authentication or join endpoints.
internal sealed class LocalSkinRuntime : IAsyncDisposable
{
    internal const string AgentUrl = "https://github.com/yushijinhun/authlib-injector/releases/download/v1.2.8/authlib-injector-1.2.8.jar";
    internal const string AgentSha256 = "9c7f4343e6c82034958ffb48c14a2cb0c85928be7283103ce17da00c6d5a7b10";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly RSA key = RSA.Create(2048);
    private readonly byte[] png;
    private readonly string name, uuid, variant;
    private readonly string prefix = "/" + Guid.NewGuid().ToString("N") + "/";
    private readonly Task worker;
    private readonly object clientsGate = new();
    private readonly HashSet<Task> clients = [];
    private readonly HttpClient http;
    private readonly string textureHash;
    internal string Root { get; }
    internal string Agent { get; }
    internal int TextureRequests;
    private LocalSkinRuntime(HttpClient http, string agent, byte[] png, string name, string uuid, string variant)
    {
        this.http = http;
        Agent = agent; this.png = png; this.name = name; this.uuid = uuid; this.variant = variant;
        textureHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        listener.Start();
        Root = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + prefix;
        worker = AcceptAsync();
    }
    internal static async Task<LocalSkinRuntime?> StartAsync(HttpClient http, AppPaths paths, OfflineAccount account, IProgress<string> progress, CancellationToken ct)
    {
        var library = new SkinLibrary(paths);
        if (library.Selected() is not { } skin) return null;
        progress.Report(L.T("Подготовка локального скина…"));
        var agent = await EnsureAgentAsync(http, paths, ct).ConfigureAwait(false);
        return new(http, agent, library.Bytes(skin), account.Name, account.Uuid, skin.Variant);
    }
    internal static async Task<string> EnsureAgentAsync(HttpClient http, AppPaths paths, CancellationToken ct)
    {
        var file = paths.FilePath("support/authlib-injector-1.2.8.jar");
        if (File.Exists(file) && new FileInfo(file).Length == 349681)
        {
            await using var existing = File.OpenRead(file);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(existing, ct).ConfigureAwait(false));
            if (hash.Equals(AgentSha256, StringComparison.OrdinalIgnoreCase)) return file;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".part";
        try
        {
            using var response = await http.GetAsync(AgentUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > 349681) throw new InvalidDataException(L.T("Контрольная сумма поддержки скинов не совпала."));
                output.Write(buffer, 0, count);
            }
            var bytes = output.ToArray();
            if (bytes.Length != 349681 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(AgentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("Контрольная сумма поддержки скинов не совпала."));
            await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
            File.Move(temporary, file, true);
            return file;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal IReadOnlyList<string> JvmArguments => [
        "-javaagent:" + Agent + "=" + Root,
        "-Dauthlibinjector.noShowServerName=true",
        "-Dauthlibinjector.mojangAntiFeatures=enabled",
        "-Dauthlibinjector.usernameCheck=enabled"
    ];
    private byte[] Metadata() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        meta = new Dictionary<string, object> { ["serverName"] = "Eclipse local skin", ["implementationName"] = "Eclipse", ["implementationVersion"] = LauncherUpdates.CurrentVersion.ToString(3),
            ["feature.no_mojang_namespace"] = true, ["feature.enable_mojang_anti_features"] = true, ["feature.enable_profile_key"] = false, ["feature.username_check"] = true },
        skinDomains = new[] { "127.0.0.1", ".minecraft.net", ".mojang.com" },
        signaturePublickey = key.ExportSubjectPublicKeyInfoPem()
    });
    private byte[] Profile()
    {
        var skin = new Dictionary<string, object> { ["url"] = Root + "textures/" + textureHash };
        if (variant == "SLIM") skin["metadata"] = new { model = "slim" };
        var texture = JsonSerializer.SerializeToUtf8Bytes(new { timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), profileId = uuid, profileName = name, textures = new { SKIN = skin } });
        var value = Convert.ToBase64String(texture);
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        return JsonSerializer.SerializeToUtf8Bytes(new { id = uuid, name, properties = new[] { new { name = "textures", value, signature } } });
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                var task = RespondAsync(client);
                lock (clientsGate) clients.Add(task);
                _ = task.ContinueWith(done => { lock (clientsGate) clients.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1];
                while (header.Length < 32768)
                {
                    if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0) return;
                    header.WriteByte(one[0]);
                    var span = header.GetBuffer(); var n = (int)header.Length;
                    if (n >= 4 && span[n - 4] == 13 && span[n - 3] == 10 && span[n - 2] == 13 && span[n - 1] == 10) break;
                }
                var text = Encoding.ASCII.GetString(header.ToArray());
                var line = text.Split("\r\n")[0].Split(' ');
                if (line.Length < 2 || !line[1].StartsWith(prefix, StringComparison.Ordinal)) return;
                var route = line[1][prefix.Length..].Split('?')[0];
                var status = 200; var mime = "application/json"; byte[] bytes;
                if (line[0] == "GET" && route == "") bytes = Metadata();
                else if (line[0] == "GET" && route == "textures/" + textureHash) { bytes = png; mime = "image/png"; Interlocked.Increment(ref TextureRequests); }
                else if (line[0] == "GET" && route == "sessionserver/session/minecraft/profile/" + uuid) bytes = Profile();
                else if (line[0] == "GET" && route.StartsWith("sessionserver/session/minecraft/profile/", StringComparison.Ordinal)
                    && System.Text.RegularExpressions.Regex.IsMatch(route[40..], "\\A[a-fA-F0-9]{32}\\z"))
                {
                    using var remote = await http.GetAsync("https://sessionserver.mojang.com/session/minecraft/profile/" + route[40..] + "?unsigned=false", timeout.Token).ConfigureAwait(false);
                    bytes = await remote.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                    if (bytes.Length > 65536) return;
                    status = (int)remote.StatusCode;
                }
                else if (line[0] == "GET" && (route.Equals("api/users/profiles/minecraft/" + name, StringComparison.OrdinalIgnoreCase) || route.Equals("minecraftservices/minecraft/profile/lookup/name/" + name, StringComparison.OrdinalIgnoreCase)))
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = uuid, name });
                else { status = 404; bytes = "{}"u8.ToArray(); }
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {mime}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        await worker.ConfigureAwait(false);
        Task[] pending; lock (clientsGate) pending = clients.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        key.Dispose(); stop.Dispose();
    }
}
