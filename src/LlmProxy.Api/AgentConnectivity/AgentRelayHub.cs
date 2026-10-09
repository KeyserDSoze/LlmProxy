using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace LlmProxy.Api.AgentConnectivity;

/// <summary>Outbound Agent-owned multiplexed HTTP relay. Sessions are deliberately local to this API replica.</summary>
public sealed class AgentRelayHub
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public async Task AttachAsync(Guid nodeId, WebSocket socket, CancellationToken cancellationToken)
    {
        var session = new Session(socket);
        if (_sessions.TryGetValue(nodeId, out var previous))
            previous.Abort("A newer agent connection replaced this session.");
        _sessions[nodeId] = session;
        try { await session.RunAsync(cancellationToken); }
        finally
        {
            if (_sessions.TryGetValue(nodeId, out var current) && ReferenceEquals(current, session))
                _sessions.TryRemove(nodeId, out _);
            session.Abort("Agent connection lost.");
        }
    }

    public void Disconnect(Guid nodeId)
    {
        if (_sessions.TryRemove(nodeId, out var session))
            session.Abort("Node identity revoked by administrator.");
    }

    public bool IsConnected(Guid nodeId) =>
        _sessions.TryGetValue(nodeId, out var session) && session.IsOpen;

    public Task<HttpResponseMessage> SendAsync(Guid nodeId, HttpRequestMessage request, CancellationToken token)
    {
        if (!_sessions.TryGetValue(nodeId, out var session) || !session.IsOpen)
            throw new HttpRequestException("Outbound node tunnel unavailable on this gateway replica.");
        return session.ForwardAsync(request, token);
    }

    public static string Root(Guid nodeId) => $"http://agent-{nodeId:N}.llmproxy.invalid";

    public static bool TryParseNode(Uri uri, out Guid nodeId)
    {
        nodeId = default;
        const string suffix = ".llmproxy.invalid";
        var host = uri.Host;
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
               host.StartsWith("agent-", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParseExact(host["agent-".Length..^suffix.Length], "N", out nodeId);
    }

    private sealed record Message(string Type, string Id, string? Method = null, string? Path = null,
        string? Body = null, int? Status = null, string? ContentType = null, string? Error = null,
        string? Authorization = null);

    private sealed class Pending
    {
        public TaskCompletionSource<Message> Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<byte[]> Chunks { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(32) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public void Fail(Exception error)
        {
            Headers.TrySetException(error);
            Chunks.Writer.TryComplete(error);
        }
    }

    private sealed class Session(WebSocket socket)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, Pending> _requests = new();
        public bool IsOpen => socket.State == WebSocketState.Open;

        public async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Remote URI missing.");
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(token);
            if (body.Length > 4 * 1024 * 1024)
                throw new HttpRequestException("The outbound relay request exceeds its 4 MiB limit.");
            var id = Guid.NewGuid().ToString("N");
            var pending = new Pending();
            if (!_requests.TryAdd(id, pending)) throw new InvalidOperationException("Duplicate relay ID.");
            var cancel = token.Register(() =>
            {
                pending.Fail(new OperationCanceledException(token));
                _requests.TryRemove(id, out _);
                _ = SendCancelAsync(id);
            });
            try
            {
                await SendAsync(new Message("request", id, request.Method.Method,
                    uri.PathAndQuery, Convert.ToBase64String(body),
                    ContentType: request.Content?.Headers.ContentType?.ToString(),
                    Authorization: request.Headers.Authorization?.ToString()), token);
                var headers = await pending.Headers.Task.WaitAsync(token);
                var response = new HttpResponseMessage((System.Net.HttpStatusCode)(headers.Status ?? 502));
                response.Content = new StreamContent(new RelayStream(pending.Chunks.Reader, cancel));
                if (!string.IsNullOrWhiteSpace(headers.ContentType))
                    response.Content.Headers.TryAddWithoutValidation("Content-Type", headers.ContentType);
                return response;
            }
            catch
            {
                cancel.Dispose();
                _requests.TryRemove(id, out _);
                throw;
            }
        }

        private async Task SendCancelAsync(string id)
        {
            try { await SendAsync(new Message("cancel", id), CancellationToken.None); }
            catch (Exception) { /* connection already gone */ }
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var message = await ReadAsync(socket, token);
                if (message is null) return;
                if (!_requests.TryGetValue(message.Id, out var pending)) continue;
                switch (message.Type)
                {
                    case "headers":
                        pending.Headers.TrySetResult(message);
                        break;
                    case "chunk" when message.Body is not null:
                        await pending.Chunks.Writer.WriteAsync(Convert.FromBase64String(message.Body), token);
                        break;
                    case "done":
                        pending.Chunks.Writer.TryComplete();
                        _requests.TryRemove(message.Id, out _);
                        break;
                    case "error":
                        pending.Fail(new HttpRequestException(message.Error ?? "Remote relay failed."));
                        _requests.TryRemove(message.Id, out _);
                        break;
                }
            }
        }

        public void Abort(string reason)
        {
            foreach (var entry in _requests)
                entry.Value.Fail(new HttpRequestException(reason));
            _requests.Clear();
            socket.Abort();
        }

        private async Task SendAsync(Message message, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            await _sendLock.WaitAsync(token);
            try
            {
                if (!IsOpen) throw new HttpRequestException("Outbound relay connection closed.");
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
            }
            finally { _sendLock.Release(); }
        }
    }

    private sealed class RelayStream(ChannelReader<byte[]> reader, CancellationTokenRegistration registration) : Stream
    {
        private byte[]? _current;
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            while (_current is null || _offset >= _current.Length)
            {
                if (!await reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!reader.TryRead(out _current)) continue;
                _offset = 0;
            }
            var length = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) registration.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() {}
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override long Seek(long value, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<Message?> ReadAsync(WebSocket socket, CancellationToken token)
    {
        using var content = new MemoryStream();
        var bytes = new byte[32768];
        while (true)
        {
            var part = await socket.ReceiveAsync(bytes.AsMemory(), token);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Invalid relay frame.");
            content.Write(bytes, 0, part.Count);
            if (content.Length > 6 * 1024 * 1024) throw new InvalidDataException("Relay message too large.");
            if (part.EndOfMessage)
                return JsonSerializer.Deserialize<Message>(content.ToArray(), Json);
        }
    }
}
