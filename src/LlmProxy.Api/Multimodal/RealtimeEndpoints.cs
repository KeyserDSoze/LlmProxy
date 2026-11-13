using System.Diagnostics;
using System.Net.WebSockets;
using LlmProxy.Api.AgentConnectivity;
using LlmProxy.Api.OpenAi;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.Multimodal;

/// <summary>
/// <c>GET /v1/realtime?model=&lt;name&gt;</c>: OpenAI-style streaming voice over WebSocket. The gateway authenticates the
/// caller (the same API key as the rest of <c>/v1</c>), applies the caller's governance and capacity, picks a deployment of a
/// <see cref="ModelSurface.Realtime"/> model and relays frames in both directions without reading them. Only runtimes the
/// gateway can reach directly are served: the server Agent relay carries HTTP, not WebSocket, so a model hosted behind it
/// answers <c>501 realtime_not_supported_through_agent_relay</c> instead of failing half-way.
/// </summary>
public static class RealtimeEndpoints
{
    public const string Path = "/v1/realtime";
    private const int FrameBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, (HttpContext context, RoutingService routing, IRequestCapacityGate capacity,
            RequestRateLimiter limiter, IRequestMetricsSink metrics, UpstreamCredentialProtector credentials) =>
            ProxyAsync(context, routing, capacity, limiter, metrics, credentials));
        return endpoints;
    }

    private static async Task ProxyAsync(HttpContext context, RoutingService routing, IRequestCapacityGate capacity,
        RequestRateLimiter limiter, IRequestMetricsSink metrics, UpstreamCredentialProtector credentials)
    {
        var requestId = Guid.NewGuid();
        var began = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var credentialId = context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var credential) && credential is Guid id ? id : (Guid?)null;
        var groupId = context.Items.TryGetValue(InferenceApiKeyMiddleware.UsageGroupIdItem, out var group) && group is Guid g ? g : (Guid?)null;
        var tenant = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerTenantIdItem, out var t) ? t as string : null;
        var owner = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerObjectIdItem, out var o) ? o as string : null;
        var enforce = context.Items.TryGetValue(InferenceApiKeyMiddleware.EnforceCallerGovernanceItem, out var e) && e is true;
        Guid? deploymentId = null, nodeId = null;
        var status = 101;
        string? error = null;
        var model = context.Request.Query["model"].ToString().Trim();

        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
        try
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                status = 426; error = "websocket_required";
                await Fail(context, status, error, "This endpoint upgrades to a WebSocket (Connection: Upgrade).");
                return;
            }
            if (model.Length is 0 or > 160)
            {
                status = 400; error = "model_required";
                await Fail(context, status, error, "Add ?model=<logical model name> to the URL.");
                return;
            }
            if (credentialId is Guid caller)
            {
                var decision = await limiter.TryAcquireAsync(caller, model, tenant, owner, groupId, enforce, DateTimeOffset.UtcNow, context.RequestAborted);
                if (!decision.Allowed)
                {
                    status = 429; error = "rate_limit_exceeded";
                    context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await Fail(context, status, error, "Request rate limit exceeded.");
                    return;
                }
            }

            var selection = await routing.SelectDetailedAsync(model, ModelSurface.Realtime, null, context.RequestAborted);
            if (selection.Route is not { } route)
            {
                var routingError = GatewayRoutingError.For(selection.Failure, model);
                status = routingError.StatusCode; error = routingError.Code;
                if (routingError.RetryAfterSeconds is int retry)
                    context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Fail(context, status, error, routingError.Message);
                return;
            }
            deploymentId = route.DeploymentId; nodeId = route.NodeId;

            if (!Uri.TryCreate(route.BaseAddress, UriKind.Absolute, out var baseAddress) || AgentRelayHub.TryParseNode(baseAddress, out _))
            {
                status = 501; error = "realtime_not_supported_through_agent_relay";
                await Fail(context, status, error, "This model is hosted behind a server Agent, which relays HTTP only. Streaming voice needs a runtime the gateway can reach directly.");
                return;
            }

            var admission = await capacity.TryAcquireAsync(route.DeploymentId, route.NodeId, route.MaxConcurrency, route.NodeMaxConcurrency, context.RequestAborted);
            if (!admission.Acquired || admission.Lease is null)
            {
                status = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable ? 503 : 429;
                error = status == 429 ? "capacity_exhausted" : "capacity_coordination_unavailable";
                await Fail(context, status, error, "Inference capacity is unavailable.");
                return;
            }

            await using var lease = admission.Lease;
            using var upstream = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(route.UpstreamBearerTokenCiphertext))
                upstream.Options.SetRequestHeader("Authorization", "Bearer " + credentials.Unprotect(route.UpstreamBearerTokenCiphertext));
            upstream.Options.SetRequestHeader("X-LlmProxy-Request-Id", requestId.ToString());
            var target = UpstreamUri(baseAddress, route.ProviderModelName);
            try { await upstream.ConnectAsync(target, context.RequestAborted); }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException)
            {
                status = 502; error = "upstream_unreachable";
                await Fail(context, status, error, "The selected runtime could not be reached.");
                return;
            }

            using var client = await context.WebSockets.AcceptWebSocketAsync();
            using var both = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.CoordinationLost);
            await RelayAsync(client, upstream, both.Token);
        }
        catch (OperationCanceledException) { error = "client_cancelled"; status = 499; }
        finally
        {
            metrics.Write(new GatewayRequestMetric(requestId, began, string.IsNullOrEmpty(model) ? "realtime" : model, "realtime", deploymentId, nodeId,
                credentialId, groupId, status, stopwatch.ElapsedMilliseconds, 1, true, null, null, null, null, null, error));
        }
    }

    /// <summary>http(s) base address of the runtime to its ws(s) realtime URL, keeping any path prefix.</summary>
    internal static Uri UpstreamUri(Uri baseAddress, string providerModel)
    {
        var builder = new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = baseAddress.AbsolutePath.TrimEnd('/') + "/v1/realtime",
            Query = "model=" + Uri.EscapeDataString(providerModel)
        };
        return builder.Uri;
    }

    /// <summary>
    /// Copies frames both ways until either side closes or fails, then closes both. Frames are not interpreted. A close is
    /// passed on as a close (never by cancelling a receive, which would abort the peer's socket without a handshake).
    /// </summary>
    internal static async Task RelayAsync(WebSocket client, WebSocket upstream, CancellationToken token)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(token);
        var toUpstream = Pump(client, upstream, both.Token);
        var toClient = Pump(upstream, client, both.Token);
        try
        {
            var finished = await Task.WhenAny(toUpstream, toClient);
            await finished; // surface a failure of the side that ended first
            // The other direction ends by itself once its peer answers the close; do not wait forever for a silent peer.
            await Task.WhenAny(Task.WhenAll(toUpstream, toClient), Task.Delay(CloseGrace, CancellationToken.None));
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException) { }
        finally
        {
            both.Cancel();
            try { await Task.WhenAll(toUpstream, toClient); } catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException) { }
            await CloseQuietly(client, WebSocketCloseStatus.NormalClosure, "closing");
            await CloseQuietly(upstream, WebSocketCloseStatus.NormalClosure, "closing");
        }
    }

    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private static async Task Pump(WebSocket from, WebSocket to, CancellationToken token)
    {
        var buffer = new byte[FrameBytes];
        while (from.State == WebSocketState.Open && to.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await from.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseQuietly(to, result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription);
                await CloseQuietly(from, result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription);
                return;
            }
            await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, token);
        }
    }

    private static async Task CloseQuietly(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, description, CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException) { }
    }

    private static async Task Fail(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = new { message, type = "invalid_request_error", code } });
    }
}
