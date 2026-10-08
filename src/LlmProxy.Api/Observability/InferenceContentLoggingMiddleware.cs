using System.Text;
using System.Text.Json;
using LlmProxy.Api.Security;
using LlmProxy.Api.SystemOne;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Api.Observability;

public sealed class InferenceContentLoggingMiddleware(RequestDelegate next)
{
    public const string StreamFailureItem = "llmproxy.inference.stream.failure";
    private static readonly HashSet<string> LoggedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/v1/chat/completions",
        "/v1/responses",
        "/v1/systemone"
    };

    public async Task InvokeAsync(HttpContext context, IInferenceContentLogSink sink)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !LoggedPaths.Contains(context.Request.Path.Value ?? string.Empty))
        {
            await next(context);
            return;
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        context.Request.EnableBuffering();

        string requestBody;
        using (var reader = new StreamReader(
                   context.Request.Body,
                   Encoding.UTF8,
                   detectEncodingFromByteOrderMarks: false,
                   leaveOpen: true))
        {
            requestBody = await reader.ReadToEndAsync(context.RequestAborted);
        }

        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
        }

        var originalBody = context.Response.Body;
        await using var capture = new ForwardingCaptureStream(originalBody, context, SurfaceFor(context.Request.Path));
        context.Response.Body = capture;
        Exception? invocationFailure = null;

        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            invocationFailure = exception;
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            var requestId = TryGetRequestId(context.Response.Headers["X-LlmProxy-Request-Id"].ToString()) ?? Guid.NewGuid();
            var apiCredentialId = context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var credentialValue) &&
                                  credentialValue is Guid credentialId
                ? credentialId
                : (Guid?)null;

            sink.Write(new InferenceContentLog(
                requestId,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                SurfaceFor(context.Request.Path),
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Items.TryGetValue(SystemOneEndpoints.LogicalModelItem, out var logicalModelValue)
                    ? logicalModelValue as string
                    : TryReadLogicalModel(requestBody),
                apiCredentialId,
                context.Response.StatusCode,
                context.Request.ContentType,
                context.Response.ContentType,
                requestBody,
                capture.GetCapturedText(
                    context.RequestAborted.IsCancellationRequested,
                    context.Items.TryGetValue(StreamFailureItem, out var failure) ? failure as string
                        : invocationFailure is not null ? "gateway_exception" : null)));
        }
    }

    private static Guid? TryGetRequestId(string value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;

    private static string SurfaceFor(PathString path)
        => path.Value?.ToLowerInvariant() switch
        {
            "/v1/chat/completions" => "chat_completions",
            "/v1/responses" => "responses",
            "/v1/systemone" => "systemone",
            _ => "unknown"
        };

    private static string? TryReadLogicalModel(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ForwardingCaptureStream(
        Stream destination,
        HttpContext context,
        string surface) : Stream
    {
        private const int MaxRawBodyBytes = 2_000_000;
        private readonly MemoryStream _rawBody = new();
        private readonly StreamingAuditAssembler _assembler = new(surface);
        private bool? _isEventStream;
        private bool _rawTruncated;
        private bool _captureFailed;

        public string GetCapturedText(bool cancelled, string? failure)
        {
            if (_isEventStream == true)
                return _assembler.Build(cancelled, _captureFailed ? "audit_reconstruction_failed" : failure);

            var raw = Encoding.UTF8.GetString(_rawBody.ToArray());
            return _rawTruncated
                ? JsonSerializer.Serialize(new { format = "llmproxy.audit.truncated.v1", truncated = true, rawBody = raw })
                : raw;
        }

        private void Capture(ReadOnlySpan<byte> bytes)
        {
            if (_captureFailed) return;
            try
            {
                _isEventStream ??= context.Response.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
                if (_isEventStream.Value)
                {
                    _assembler.Append(bytes);
                }
                else
                {
                    var available = MaxRawBodyBytes - (int)_rawBody.Length;
                    if (bytes.Length > available) _rawTruncated = true;
                    if (available > 0) _rawBody.Write(bytes[..Math.Min(available, bytes.Length)]);
                }
            }
            catch (Exception)
            {
                // Audit is best-effort and must never break or delay client inference.
                _captureFailed = true;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => destination.CanWrite;
        public override long Length => _rawBody.Length;
        public override long Position { get => _rawBody.Position; set => throw new NotSupportedException(); }

        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            destination.Write(buffer, offset, count);
            Capture(buffer.AsSpan(offset, count));
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(buffer, cancellationToken);
            Capture(buffer.Span);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            Capture(buffer.AsSpan(offset, count));
        }
    }
}
