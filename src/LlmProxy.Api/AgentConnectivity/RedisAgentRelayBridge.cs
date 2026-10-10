using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using LlmProxy.Infrastructure.Runtime;
using LlmProxy.Infrastructure.Security;
using StackExchange.Redis;

namespace LlmProxy.Api.AgentConnectivity;

/// <summary>Transient authenticated cross-replica forwarding. Never stores inference content in Redis keys.</summary>
public sealed class RedisAgentRelayBridge(
    RedisCoordinationConnection redis, SensitiveDataProtector crypto, AgentRelayHub local,
    ILogger<RedisAgentRelayBridge> logger) : BackgroundService
{
    private const string Purpose = "agent-relay-v1";
    private const int MaxRequestBytes = 4 * 1024 * 1024;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private ISubscriber? _subscriber;
    private volatile bool _ready;
    private sealed record Frame(string Type, Guid NodeId, string Id, string? Owner = null,
        string? Reply = null, string? Method = null, string? Path = null,
        string? ContentType = null, string? Body = null, string? Authorization = null,
        int? Status = null, string? Error = null);

    private sealed class Waiting
    {
        public TaskCompletionSource<Frame> Headers { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<byte[]> Chunks { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        public void Fail(Exception error) { Headers.TrySetException(error); Chunks.Writer.TryComplete(error); }
    }

    private string Queue(string owner) => redis.Key("agent-relay:req:" + owner);
    private string OwnerKey(Guid id) => redis.Key("agent-relay:owner:" + id.ToString("N"));
    private string Reply(string id) => redis.Key("agent-relay:reply:" + id);

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sub = await redis.GetSubscriberAsync(token);
                _subscriber = sub;
                var channel = RedisChannel.Literal(Queue(_instance));
                await sub.SubscribeAsync(channel, (_, value) =>
                {
                    Frame? frame;
                    try { frame = Decode(value); }
                    catch (Exception e) { logger.LogWarning("Rejected malformed relay frame: {Type}", e.GetType().Name); return; }
                    if (frame is null || frame.Owner != _instance) return;
                    if (frame.Type == "cancel")
                    {
                        if (_active.TryGetValue(frame.Id, out var current)) current.Cancel();
                        return;
                    }
                    if (frame.Type == "request" && local.IsConnected(frame.NodeId))
                        _ = Task.Run(() => ServeAsync(frame, token), CancellationToken.None);
                });
                _ready = true;
                using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(3));
                do
                {
                    var db = await redis.GetDatabaseAsync(token);
                    foreach (var node in local.ConnectedNodes())
                        await db.StringSetAsync(OwnerKey(node), _instance, TimeSpan.FromSeconds(10));
                } while (await ticker.WaitForNextTickAsync(token));
                _ready = false;
                await sub.UnsubscribeAsync(channel);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                _ready = false;
                logger.LogWarning("Agent relay Redis subscription unavailable: {Type}", e.GetType().Name);
                try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
            }
        }
        _ready = false;
    }

    public async Task<bool> HasOwnerAsync(Guid nodeId, CancellationToken token)
    {
        if (local.IsConnected(nodeId)) return true;
        if (!_ready) return false;
        var db = await redis.GetDatabaseAsync(token);
        return (await db.StringGetAsync(OwnerKey(nodeId))).HasValue;
    }

    public async Task<HttpResponseMessage> ForwardAsync(Guid nodeId, HttpRequestMessage request, CancellationToken token)
    {
        if (local.IsConnected(nodeId)) return await local.SendAsync(nodeId, request, token);
        if (!_ready) throw new HttpRequestException("Distributed Agent transport unavailable.");
        var db = await redis.GetDatabaseAsync(token);
        var owner = await db.StringGetAsync(OwnerKey(nodeId));
        if (!owner.HasValue) throw new HttpRequestException("No connected gateway owns this Agent.");
        var uri = request.RequestUri ?? throw new HttpRequestException("Missing Agent endpoint.");
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(token);
        if (body.Length > MaxRequestBytes) throw new HttpRequestException("Agent relay body exceeds 4 MiB.");

        var id = Guid.NewGuid().ToString("N");
        var receiver = RedisChannel.Literal(Reply(id));
        var sub = _subscriber ?? throw new HttpRequestException("Redis subscription unavailable.");
        var pending = new Waiting();
        var closed = 0;
        async Task CloseAsync(bool notifyOwner)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { await sub.UnsubscribeAsync(receiver); } catch (Exception) { }
            if (notifyOwner)
            {
                try { await sub.PublishAsync(RedisChannel.Literal(Queue(owner.ToString())),
                    Encode(new Frame("cancel", nodeId, id, Owner: owner.ToString()))); }
                catch (Exception) { }
            }
        }
        await sub.SubscribeAsync(receiver, (_, value) =>
        {
            Frame? frame;
            try { frame = Decode(value); }
            catch (Exception e) { pending.Fail(new IOException("Invalid relay response frame.", e)); return; }
            if (frame?.Id != id || frame.NodeId != nodeId) return;
            switch (frame.Type)
            {
                case "headers": pending.Headers.TrySetResult(frame); break;
                case "chunk" when frame.Body is not null:
                    try
                    {
                        if (!pending.Chunks.Writer.TryWrite(Convert.FromBase64String(frame.Body)))
                            pending.Fail(new IOException("Agent stream consumer is too slow."));
                    }
                    catch (FormatException e) { pending.Fail(new IOException("Invalid Agent stream chunk.", e)); }
                    break;
                case "done": pending.Chunks.Writer.TryComplete(); break;
                case "error": pending.Fail(new IOException(frame.Error ?? "Remote Agent transport failed.")); break;
            }
        });
        try
        {
            var outbound = new Frame("request", nodeId, id, Owner: owner.ToString(), Reply: Reply(id),
                Method: request.Method.Method, Path: uri.PathAndQuery,
                ContentType: request.Content?.Headers.ContentType?.ToString(),
                Body: Convert.ToBase64String(body), Authorization: request.Headers.Authorization?.ToString());
            if (await sub.PublishAsync(RedisChannel.Literal(Queue(owner.ToString())), Encode(outbound)) == 0)
                throw new HttpRequestException("Agent owner is no longer listening.");
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersTimeout.CancelAfter(TimeSpan.FromMinutes(30));
            var headers = await pending.Headers.Task.WaitAsync(headersTimeout.Token);
            var response = new HttpResponseMessage((HttpStatusCode)(headers.Status ?? 502))
            {
                Content = new StreamContent(new RemoteStream(pending.Chunks.Reader, () => CloseAsync(true)))
            };
            if (!string.IsNullOrWhiteSpace(headers.ContentType))
                response.Content.Headers.TryAddWithoutValidation("Content-Type", headers.ContentType);
            return response;
        }
        catch
        {
            await CloseAsync(true);
            throw;
        }
    }

    private async Task ServeAsync(Frame frame, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!_active.TryAdd(frame.Id, lifetime)) return;
        try
        {
            var path = frame.Path ?? throw new InvalidDataException("Missing path.");
            if (path.Length > 8192 || !path.StartsWith("/", StringComparison.Ordinal) ||
                path.Contains("..", StringComparison.Ordinal) ||
                (!path.StartsWith("/management/", StringComparison.Ordinal) &&
                 !path.StartsWith("/runtime/", StringComparison.Ordinal)))
                throw new InvalidDataException("Remote path not allowed.");
            using var request = new HttpRequestMessage(new HttpMethod(frame.Method ?? "GET"),
                AgentRelayHub.Root(frame.NodeId) + path);
            if (frame.Body is not null)
            {
                var bytes = Convert.FromBase64String(frame.Body);
                if (bytes.Length > MaxRequestBytes) throw new InvalidDataException("Oversized request.");
                request.Content = new ByteArrayContent(bytes);
                if (!string.IsNullOrWhiteSpace(frame.ContentType))
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", frame.ContentType);
            }
            if (!string.IsNullOrWhiteSpace(frame.Authorization))
            {
                if (!frame.Authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                    frame.Authorization.Length > 4096) throw new InvalidDataException("Unsupported auth.");
                request.Headers.TryAddWithoutValidation("Authorization", frame.Authorization);
            }
            using var response = await local.SendAsync(frame.NodeId, request, lifetime.Token);
            await PublishAsync(new Frame("headers", frame.NodeId, frame.Id, Status: (int)response.StatusCode,
                ContentType: response.Content.Headers.ContentType?.ToString()), frame.Reply!);
            await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
            var buffer = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), lifetime.Token);
                if (count == 0) break;
                await PublishAsync(new Frame("chunk", frame.NodeId, frame.Id,
                    Body: Convert.ToBase64String(buffer, 0, count)), frame.Reply!);
            }
            await PublishAsync(new Frame("done", frame.NodeId, frame.Id), frame.Reply!);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            try { await PublishAsync(new Frame("error", frame.NodeId, frame.Id, Error: e.GetType().Name), frame.Reply!); }
            catch (Exception) { }
        }
        finally { _active.TryRemove(frame.Id, out _); }
    }

    private string Encode(Frame frame) =>
        crypto.Protect(JsonSerializer.Serialize(frame, AgentRelayHub.Json), Purpose);

    private Frame? Decode(RedisValue value) =>
        JsonSerializer.Deserialize<Frame>(crypto.Unprotect(value.ToString(), Purpose), AgentRelayHub.Json);

    private Task<long> PublishAsync(Frame frame, string channel) =>
        (_subscriber ?? throw new InvalidOperationException("Relay bus unavailable."))
            .PublishAsync(RedisChannel.Literal(channel), Encode(frame));

    private sealed class RemoteStream(ChannelReader<byte[]> source, Func<Task> close) : Stream
    {
        private byte[]? _chunk;
        private int _position;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            while (_chunk is null || _position >= _chunk.Length)
            {
                if (!await source.WaitToReadAsync(ct)) return 0;
                if (!source.TryRead(out _chunk)) continue;
                _position = 0;
            }
            var count = Math.Min(buffer.Length, _chunk.Length - _position);
            _chunk.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _ = close();
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
