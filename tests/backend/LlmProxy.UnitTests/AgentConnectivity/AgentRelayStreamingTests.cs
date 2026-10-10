using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LlmProxy.Api.AgentConnectivity;

namespace LlmProxy.UnitTests.AgentConnectivity;

public sealed class AgentRelayStreamingTests
{
    [Fact]
    public async Task Outbound_websocket_preserves_streaming_http_chunks_and_provider_authorization()
    {
        var hub = new AgentRelayHub();
        var node = Guid.NewGuid();
        using var socket = new InMemorySocket();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var accepting = hub.AttachAsync(node, socket, lifetime.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            AgentRelayHub.Root(node) + "/runtime/18000/v1/chat/completions");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "provider-secret");
        request.Content = new StringContent("{\"model\":\"example\"}", Encoding.UTF8, "application/json");
        var forwarding = hub.SendAsync(node, request, lifetime.Token);
        var payload = await socket.Sent.Reader.ReadAsync(lifetime.Token);
        using var decoded = JsonDocument.Parse(payload);
        var id = decoded.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("request", decoded.RootElement.GetProperty("type").GetString());
        Assert.Equal("Bearer provider-secret", decoded.RootElement.GetProperty("authorization").GetString());
        socket.Inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { type = "headers", id, status = 200, contentType = "text/event-stream" })));
        using var response = await forwarding;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var text = response.Content.ReadAsStringAsync(lifetime.Token);
        socket.Inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { type = "chunk", id,
                body = Convert.ToBase64String(Encoding.UTF8.GetBytes("data: {\"choices\":[]}\n\n")) })));
        socket.Inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { type = "chunk", id,
                body = Convert.ToBase64String(Encoding.UTF8.GetBytes("data: [DONE]\n\n")) })));
        socket.Inbound.Writer.TryWrite(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "done", id })));
        Assert.Equal("data: {\"choices\":[]}\n\ndata: [DONE]\n\n", await text);
        lifetime.Cancel();
        // A session can finish normally if cancellation is observed between
        // reads, or throw if a pending receive observes the canceled token.
        // Both paths must remove the disconnected Agent from the gateway.
        try { await accepting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        Assert.False(hub.IsConnected(node));
    }

    private sealed class InMemorySocket : WebSocket
    {
        public Channel<byte[]> Inbound { get; } = Channel.CreateUnbounded<byte[]>();
        public Channel<byte[]> Sent { get; } = Channel.CreateUnbounded<byte[]>();
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            Inbound.Writer.TryComplete();
        }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken token)
        {
            _state = WebSocketState.Closed;
            Inbound.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken token)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }
        public override void Dispose() => Abort();
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var bytes = await Inbound.Reader.ReadAsync(token);
            if (bytes.Length > buffer.Count) throw new InvalidDataException("Oversized fake frame.");
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken token)
        {
            if (messageType != WebSocketMessageType.Text) throw new InvalidOperationException();
            Sent.Writer.TryWrite(buffer.AsSpan().ToArray());
            return Task.CompletedTask;
        }
    }
}
