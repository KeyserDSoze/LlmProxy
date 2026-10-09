using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.NodeAgent;

public sealed class OutboundAgentRelayWorker(
    NodeAgentOptions options, ManagedModelRegistry registry, IHttpClientFactory factory,
    ILogger<OutboundAgentRelayWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Connection(Guid NodeId, string AgentSecret);
    private sealed record Frame(string Type, string Id, string? Method = null, string? Path = null,
        string? Body = null, int? Status = null, string? ContentType = null, string? Error = null,
        string? Authorization = null);

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (options.ConnectionMode != "outbound" || string.IsNullOrWhiteSpace(options.GatewayBaseAddress)) return;
        var file = Path.Combine(options.DataDirectory, "gateway-connection.json");
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(file)) { await Task.Delay(2000, token); continue; }
                var pair = JsonSerializer.Deserialize<Connection>(await File.ReadAllTextAsync(file, token), Json);
                if (pair is null || pair.NodeId == Guid.Empty || !pair.AgentSecret.StartsWith("lpa_", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invalid enrollment state.");
                var uri = new Uri(options.GatewayBaseAddress!.TrimEnd('/') + "/api/agent-connection/" + pair.NodeId + "/tunnel");
                if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                    throw new InvalidOperationException("Remote gateways must use HTTPS.");
                var wsUri = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" }.Uri;
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + pair.AgentSecret);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await socket.ConnectAsync(wsUri, token);
                logger.LogInformation("Outbound management/inference tunnel connected.");
                await PumpAsync(socket, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Outbound tunnel reconnect: {Reason}", e.Message); }
            try { await Task.Delay(3000, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PumpAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var sender = new SemaphoreSlim(1, 1);
        var active = new ConcurrentDictionary<string, CancellationTokenSource>();
        try
        {
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(socket, linked.Token);
                if (frame is null) break;
                if (frame.Type == "cancel")
                {
                    if (active.TryGetValue(frame.Id, out var prior)) prior.Cancel();
                    continue;
                }
                if (frame.Type != "request") continue;
                var child = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                if (!active.TryAdd(frame.Id, child)) { child.Dispose(); continue; }
                _ = Task.Run(async () =>
                {
                    try { await ExecuteAsync(frame, socket, sender, child.Token); }
                    catch (OperationCanceledException) when (child.IsCancellationRequested) {}
                    catch (Exception ex)
                    {
                        try { await SendAsync(socket, sender, new Frame("error", frame.Id, Error: ex.GetType().Name), linked.Token); }
                        catch (Exception) {}
                    }
                    finally { active.TryRemove(frame.Id, out _); child.Dispose(); }
                });
            }
        }
        finally
        {
            linked.Cancel();
            foreach (var child in active.Values) child.Cancel();
        }
    }

    private async Task ExecuteAsync(Frame frame, ClientWebSocket socket, SemaphoreSlim sender, CancellationToken token)
    {
        var path = frame.Path ?? throw new InvalidDataException("Missing path.");
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid remote path.");
        int port;
        string localPath;
        bool isManagement;
        if (path.StartsWith("/management/", StringComparison.Ordinal))
        {
            port = 9900;
            localPath = path["/management".Length..];
            isManagement = true;
            var valid = (frame.Method == "GET" && localPath is "/health" or "/v1/system" or "/v1/models") ||
                (frame.Method == "POST" && (localPath == "/v1/models/install" ||
                    (localPath.StartsWith("/v1/models/", StringComparison.Ordinal) &&
                     (localPath.EndsWith("/start", StringComparison.Ordinal) || localPath.EndsWith("/stop", StringComparison.Ordinal))))) ||
                (frame.Method == "DELETE" && localPath.StartsWith("/v1/models/", StringComparison.Ordinal));
            if (!valid) throw new InvalidDataException("Management path not allowed.");
        }
        else if (path.StartsWith("/runtime/", StringComparison.Ordinal))
        {
            var rest = path["/runtime/".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 1 || !int.TryParse(rest[..slash], out port) || port is < 1024 or > 65535)
                throw new InvalidDataException("Invalid runtime port.");
            localPath = rest[slash..];
            isManagement = false;
            if (!localPath.StartsWith("/v1/", StringComparison.Ordinal) &&
                !localPath.StartsWith("/health", StringComparison.Ordinal) &&
                !localPath.StartsWith("/metrics", StringComparison.Ordinal))
                throw new InvalidDataException("Runtime route denied.");
            var managed = await registry.ReadAsync(token);
            if (!managed.Any(x => x.Port == port)) throw new InvalidDataException("Unknown installation.");
        }
        else throw new InvalidDataException("Unknown route.");

        if (frame.Method is not ("GET" or "POST" or "DELETE" or "PUT"))
            throw new InvalidDataException("Unsupported method.");
        var body = frame.Body is null ? [] : Convert.FromBase64String(frame.Body);
        if (body.Length > 4 * 1024 * 1024) throw new InvalidDataException("Body limit.");
        using var outbound = new HttpRequestMessage(new HttpMethod(frame.Method),
            new Uri($"http://127.0.0.1:{port}" + localPath));
        if (body.Length > 0)
        {
            outbound.Content = new ByteArrayContent(body);
            if (!string.IsNullOrWhiteSpace(frame.ContentType))
                outbound.Content.Headers.TryAddWithoutValidation("Content-Type", frame.ContentType);
        }
        if (isManagement && options.BearerToken is not null)
            outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
        else if (!isManagement && frame.Authorization is not null)
        {
            if (frame.Authorization.Length > 4096 ||
                !frame.Authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                frame.Authorization.Contains('\r') || frame.Authorization.Contains('\n'))
                throw new InvalidDataException("Unsupported runtime authentication header.");
            outbound.Headers.TryAddWithoutValidation("Authorization", frame.Authorization);
        }
        var client = factory.CreateClient("relay-local");
        using var result = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, token);
        await SendAsync(socket, sender, new Frame("headers", frame.Id,
            Status: (int)result.StatusCode, ContentType: result.Content.Headers.ContentType?.ToString()), token);
        await using var data = await result.Content.ReadAsStreamAsync(token);
        var buffer = new byte[16384];
        while (true)
        {
            var count = await data.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            await SendAsync(socket, sender, new Frame("chunk", frame.Id, Body: Convert.ToBase64String(buffer, 0, count)), token);
        }
        await SendAsync(socket, sender, new Frame("done", frame.Id), token);
    }

    private static async Task SendAsync(ClientWebSocket socket, SemaphoreSlim gate, Frame message, CancellationToken ct)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await gate.WaitAsync(ct);
        try { await socket.SendAsync(content.AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { gate.Release(); }
    }

    private static async Task<Frame?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[32768];
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Invalid relay frame.");
            memory.Write(buffer, 0, part.Count);
            if (memory.Length > 6 * 1024 * 1024) throw new InvalidDataException("Message too large.");
            if (part.EndOfMessage) return JsonSerializer.Deserialize<Frame>(memory.ToArray(), Json);
        }
    }
}
