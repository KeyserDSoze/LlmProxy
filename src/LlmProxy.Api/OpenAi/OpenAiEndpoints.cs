using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Api.OpenAi;

public static class OpenAiEndpoints
{
    private const int MaxUpstreamAttempts = 3;

    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", async (IDeploymentCatalog catalog, CancellationToken cancellationToken) =>
        {
            var models = (await catalog.GetAvailableModelsAsync(cancellationToken))
                .Where(model => model.Surface != ModelSurface.SystemOne);
            return Results.Ok(new
            {
                @object = "list",
                data = models.Select(model => new
                {
                    id = model.PublicName,
                    @object = "model",
                    created = 0,
                    owned_by = "llmproxy",
                    llmproxy_surface = model.Surface.ToString()
                })
            });
        });

        // Same catalog as /v1/models, with what a client needs to use each model well: limits, input/output modalities,
        // endpoints and declared capabilities. Kept separate so /v1/models stays exactly what OpenAI clients expect.
        endpoints.MapGet("/v1/models/details", async (HttpContext context, IDeploymentCatalog catalog, CancellationToken cancellationToken) =>
        {
            var type = context.Request.Query["type"].ToString();
            var models = (await catalog.GetAvailableModelsAsync(cancellationToken))
                .Where(model => model.Surface != ModelSurface.SystemOne)
                .Where(model => type.Length == 0 || string.Equals(ModelDetails.TypeOf(model.Surface), type, StringComparison.OrdinalIgnoreCase));
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @object = "list",
                data = models.Select(ModelDetails.From)
            }, ModelDetails.Json);

            // The list is identical for every caller and changes rarely: let clients revalidate instead of re-downloading.
            var etag = "\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body))[..32] + "\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "private, max-age=30";
            if (context.Request.Headers.IfNoneMatch.Any(value => value == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(body, "application/json");
        });

        // Every modality (text, images, speech, transcription, embeddings, voice, classifiers) with the models running for it.
        // Not part of the OpenAI API: a convenience for applications that need to discover what this gateway offers.
        endpoints.MapGet("/v1/modalities", async (HttpContext context, IDeploymentCatalog catalog, CancellationToken cancellationToken) =>
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(
                ModelDetails.Modalities(await catalog.GetAvailableModelsAsync(cancellationToken)), ModelDetails.Json);
            var etag = "\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body))[..32] + "\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "private, max-age=30";
            if (context.Request.Headers.IfNoneMatch.Any(value => value == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(body, "application/json");
        });

        endpoints.MapPost("/v1/chat/completions", ForwardChatCompletionsAsync);
        endpoints.MapPost("/v1/responses", ForwardResponsesAsync);
        return endpoints;
    }

    private static Task ForwardChatCompletionsAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestCapacityGate capacityGate,
        IDeploymentPerformanceTracker performanceTracker,
        RequestRateLimiter rateLimiter,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory,
        UpstreamCredentialProtector upstreamCredentialProtector) =>
        ForwardInferenceAsync(
            context,
            routingService,
            capacityGate,
            performanceTracker,
            rateLimiter,
            metricsSink,
            httpClientFactory,
            upstreamCredentialProtector,
            "/v1/chat/completions");

    private static Task ForwardResponsesAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestCapacityGate capacityGate,
        IDeploymentPerformanceTracker performanceTracker,
        RequestRateLimiter rateLimiter,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory,
        UpstreamCredentialProtector upstreamCredentialProtector) =>
        ForwardInferenceAsync(
            context,
            routingService,
            capacityGate,
            performanceTracker,
            rateLimiter,
            metricsSink,
            httpClientFactory,
            upstreamCredentialProtector,
            "/v1/responses");

    private static async Task ForwardInferenceAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestCapacityGate capacityGate,
        IDeploymentPerformanceTracker performanceTracker,
        RequestRateLimiter rateLimiter,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory,
        UpstreamCredentialProtector upstreamCredentialProtector,
        string upstreamPath)
    {
        var requestId = Guid.NewGuid();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LlmProxy.Api.OpenAi.Inference");
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var surface = upstreamPath.EndsWith("/responses", StringComparison.Ordinal)
            ? "responses"
            : "chat_completions";
        var apiCredentialId = context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var credentialValue) && credentialValue is Guid credentialId
            ? credentialId
            : (Guid?)null;
        var usageGroupId = context.Items.TryGetValue(InferenceApiKeyMiddleware.UsageGroupIdItem, out var groupValue) && groupValue is Guid groupId
            ? groupId
            : (Guid?)null;
        var ownerTenantId = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerTenantIdItem, out var tenantValue)
            ? tenantValue as string
            : null;
        var ownerObjectId = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerObjectIdItem, out var ownerValue)
            ? ownerValue as string
            : null;
        var enforceCallerGovernance =
            context.Items.TryGetValue(InferenceApiKeyMiddleware.EnforceCallerGovernanceItem, out var governanceValue) &&
            governanceValue is true;
        Guid? finalDeploymentId = null;
        Guid? finalNodeId = null;
        var finalStatusCode = StatusCodes.Status500InternalServerError;
        var attemptCount = 0;
        var isStreaming = false;
        long? upstreamHeaderMilliseconds = null;
        long? timeToFirstByteMilliseconds = null;
        TokenUsage? tokenUsage = null;
        string? finalErrorCode = null;
        string? publicModelName = null;
        var routingFailure = RoutingSelectionFailure.Unavailable;
        var capacityRaceObserved = false;
        var capacityCoordinationUnavailable = false;

        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();

        try
        {
            string rawBody;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            {
                rawBody = await reader.ReadToEndAsync(context.RequestAborted);
            }

            if (!OpenAiRequestPayload.TryParse(
                    rawBody,
                    out var requestObject,
                    out publicModelName,
                    out var parseErrorCode,
                    out var parseErrorMessage))
            {
                finalStatusCode = StatusCodes.Status400BadRequest;
                finalErrorCode = parseErrorCode;
                await WriteGatewayErrorAsync(
                    context,
                    finalStatusCode,
                    "invalid_request_error",
                    parseErrorCode,
                    parseErrorMessage);
                return;
            }

            if (apiCredentialId is Guid callerCredentialId)
            {
                var rateLimitDecision = await rateLimiter.TryAcquireAsync(
                    callerCredentialId,
                    publicModelName,
                    ownerTenantId,
                    ownerObjectId,
                    usageGroupId,
                    enforceCallerGovernance,
                    DateTimeOffset.UtcNow,
                    context.RequestAborted);
                if (!rateLimitDecision.Allowed)
                {
                    finalStatusCode = StatusCodes.Status429TooManyRequests;
                    finalErrorCode = "rate_limit_exceeded";
                    context.Response.Headers.RetryAfter = rateLimitDecision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await WriteGatewayErrorAsync(
                        context,
                        finalStatusCode,
                        "rate_limit_error",
                        finalErrorCode,
                        $"Request rate limit exceeded for model '{publicModelName}'. Retry after {rateLimitDecision.RetryAfterSeconds} seconds.");
                    return;
                }
            }

            var excluded = new HashSet<Guid>();
            var client = httpClientFactory.CreateClient("vllm");

            for (var attempt = 1; attempt <= MaxUpstreamAttempts; attempt++)
            {
                var selection = await routingService.SelectDetailedAsync(publicModelName, ModelSurface.OpenAi, excluded, context.RequestAborted);
                var route = selection.Route;
                if (route is null)
                {
                    routingFailure = selection.Failure;
                    break;
                }

                var admission = await capacityGate.TryAcquireAsync(
                    route.DeploymentId,
                    route.NodeId,
                    route.MaxConcurrency,
                    route.NodeMaxConcurrency,
                    context.RequestAborted);

                if (!admission.Acquired || admission.Lease is null)
                {
                    if (admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable)
                    {
                        capacityCoordinationUnavailable = true;
                        break;
                    }

                    capacityRaceObserved = true;
                    excluded.Add(route.DeploymentId);
                    attempt--;
                    continue;
                }

                await using var lease = admission.Lease;
                using var inferenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted,
                    lease.CoordinationLost);
                var inferenceCancellationToken = inferenceCancellation.Token;

                attemptCount++;
                finalDeploymentId = route.DeploymentId;
                finalNodeId = route.NodeId;
                OpenAiRequestPayload.RewriteModel(requestObject, route.ProviderModelName);

                using var outbound = CreateOutboundRequest(context.Request, requestObject, route, upstreamPath, requestId, upstreamCredentialProtector);
                HttpResponseMessage upstream;
                var attemptStartedMilliseconds = stopwatch.ElapsedMilliseconds;
                var upstreamStopwatch = Stopwatch.StartNew();

                var upstreamTimeouts = UpstreamTimeouts.From(context.RequestServices.GetRequiredService<IConfiguration>());
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(inferenceCancellationToken);
                headerTimeout.CancelAfter(upstreamTimeouts.ResponseHeaders);

                try
                {
                    upstream = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
                    upstreamHeaderMilliseconds = upstreamStopwatch.ElapsedMilliseconds;
                }
                catch (OperationCanceledException) when (
                    lease.CoordinationLost.IsCancellationRequested &&
                    !context.RequestAborted.IsCancellationRequested)
                {
                    finalStatusCode = StatusCodes.Status503ServiceUnavailable;
                    finalErrorCode = "capacity_lease_lost";
                    performanceTracker.Observe(
                        route.DeploymentId,
                        infrastructureHealthy: false,
                        upstreamStopwatch.ElapsedMilliseconds,
                        timeToFirstByteMilliseconds: null,
                        DateTimeOffset.UtcNow);
                    await HandleCapacityLeaseLostAsync(context, requestId);
                    return;
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    finalStatusCode = 499;
                    finalErrorCode = "client_cancelled";
                    return;
                }
                catch (OperationCanceledException) when (headerTimeout.IsCancellationRequested)
                {
                    // Neither the client nor the capacity lease cancelled: the node never answered in time.
                    finalStatusCode = StatusCodes.Status504GatewayTimeout;
                    finalErrorCode = "upstream_timeout";
                    performanceTracker.Observe(
                        route.DeploymentId,
                        infrastructureHealthy: false,
                        upstreamStopwatch.ElapsedMilliseconds,
                        timeToFirstByteMilliseconds: null,
                        DateTimeOffset.UtcNow);
                    logger.LogWarning("Inference runtime on node {NodeName} sent no response headers within {Seconds}s for request {RequestId}.",
                        route.NodeName, (int)upstreamTimeouts.ResponseHeaders.TotalSeconds, requestId);

                    if (attempt < MaxUpstreamAttempts)
                    {
                        excluded.Add(route.DeploymentId);
                        continue;
                    }

                    await WriteGatewayErrorAsync(
                        context,
                        finalStatusCode,
                        "gateway_error",
                        finalErrorCode,
                        $"The inference runtime for model '{publicModelName}' did not answer in time. Retry shortly.");
                    return;
                }
                catch (HttpRequestException exception)
                {
                    finalStatusCode = StatusCodes.Status502BadGateway;
                    finalErrorCode = "upstream_unreachable";
                    performanceTracker.Observe(
                        route.DeploymentId,
                        infrastructureHealthy: false,
                        upstreamStopwatch.ElapsedMilliseconds,
                        timeToFirstByteMilliseconds: null,
                        DateTimeOffset.UtcNow);

                    if (attempt < MaxUpstreamAttempts)
                    {
                        excluded.Add(route.DeploymentId);
                        continue;
                    }

                    // Server names, addresses and transport errors stay in the internal log, not in the client response.
                    logger.LogWarning("Inference runtime on node {NodeName} could not be reached for request {RequestId}: {Error}",
                        route.NodeName, requestId, exception.Message);
                    await WriteGatewayErrorAsync(
                        context,
                        finalStatusCode,
                        "gateway_error",
                        finalErrorCode,
                        $"The inference runtime for model '{publicModelName}' could not be reached. Retry shortly.");
                    return;
                }

                using (upstream)
                {
                    finalStatusCode = (int)upstream.StatusCode;

                    if ((int)upstream.StatusCode >= 500 && attempt < MaxUpstreamAttempts)
                    {
                        performanceTracker.Observe(
                            route.DeploymentId,
                            infrastructureHealthy: false,
                            upstreamStopwatch.ElapsedMilliseconds,
                            timeToFirstByteMilliseconds: null,
                            DateTimeOffset.UtcNow);
                        excluded.Add(route.DeploymentId);
                        finalErrorCode = "upstream_server_error";
                        continue;
                    }

                    context.Response.StatusCode = finalStatusCode;
                    CopyResponseHeaders(upstream, context.Response);
                    var observer = new OpenAiResponseObserver(
                        IsEventStream(upstream),
                        () => stopwatch.ElapsedMilliseconds);

                    try
                    {
                        await CopyUpstreamBodyAsync(upstream, context, observer, upstreamTimeouts.StreamIdle, inferenceCancellationToken);
                        finalErrorCode = upstream.IsSuccessStatusCode ? null : "upstream_error";
                        ObserveCompletedAttempt(
                            performanceTracker,
                            route.DeploymentId,
                            infrastructureHealthy: finalStatusCode < 500,
                            stopwatch,
                            attemptStartedMilliseconds,
                            observer.TimeToFirstByteMilliseconds);
                        return;
                    }
                    catch (OperationCanceledException) when (
                        lease.CoordinationLost.IsCancellationRequested &&
                        !context.RequestAborted.IsCancellationRequested)
                    {
                        finalStatusCode = StatusCodes.Status503ServiceUnavailable;
                        finalErrorCode = "capacity_lease_lost";
                        ObserveCompletedAttempt(
                            performanceTracker,
                            route.DeploymentId,
                            infrastructureHealthy: false,
                            stopwatch,
                            attemptStartedMilliseconds,
                            observer.TimeToFirstByteMilliseconds);
                        await HandleCapacityLeaseLostAsync(context, requestId);
                        return;
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                    {
                        finalStatusCode = 499;
                        finalErrorCode = "client_cancelled";
                        return;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException)
                    {
                        finalErrorCode = "upstream_stream_interrupted";
                        ObserveCompletedAttempt(
                            performanceTracker,
                            route.DeploymentId,
                            infrastructureHealthy: false,
                            stopwatch,
                            attemptStartedMilliseconds,
                            observer.TimeToFirstByteMilliseconds);

                        if (context.Response.HasStarted)
                        {
                            context.Items[LlmProxy.Api.Observability.InferenceContentLoggingMiddleware.StreamFailureItem] = finalErrorCode;
                            context.Abort();
                            return;
                        }

                        finalStatusCode = StatusCodes.Status502BadGateway;
                        logger.LogWarning("Inference response from node {NodeName} was interrupted before the first byte for request {RequestId}: {Error}",
                            route.NodeName, requestId, exception.Message);
                        // Headers were copied from the upstream response: drop them so a stale Content-Length or
                        // Content-Encoding cannot corrupt the gateway's own error body.
                        context.Response.Clear();
                        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
                        await WriteGatewayErrorAsync(
                            context,
                            finalStatusCode,
                            "gateway_error",
                            finalErrorCode,
                            $"The inference response for model '{publicModelName}' was interrupted. Retry shortly.");
                        return;
                    }
                    finally
                    {
                        observer.Complete();
                        isStreaming = observer.IsStreaming;
                        timeToFirstByteMilliseconds = observer.TimeToFirstByteMilliseconds;
                        tokenUsage = observer.Usage;
                    }
                }
            }

            if (capacityCoordinationUnavailable)
            {
                finalStatusCode = StatusCodes.Status503ServiceUnavailable;
                finalErrorCode = "capacity_coordination_unavailable";
                await WriteGatewayErrorAsync(
                    context,
                    finalStatusCode,
                    "gateway_unavailable",
                    finalErrorCode,
                    "Distributed inference capacity coordination is temporarily unavailable. Retry shortly.");
                return;
            }

            var routingError = GatewayRoutingError.For(
                capacityRaceObserved ? RoutingSelectionFailure.CapacityExhausted : routingFailure,
                publicModelName ?? string.Empty);
            finalStatusCode = routingError.StatusCode;
            finalErrorCode = routingError.Code;
            if (routingError.RetryAfterSeconds is int retryAfter)
            {
                context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await WriteGatewayErrorAsync(context, routingError.StatusCode, routingError.Type, routingError.Code, routingError.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(publicModelName))
            {
                metricsSink.Write(new GatewayRequestMetric(
                    requestId,
                    startedAtUtc,
                    publicModelName,
                    surface,
                    finalDeploymentId,
                    finalNodeId,
                    apiCredentialId,
                    usageGroupId,
                    finalStatusCode,
                    stopwatch.ElapsedMilliseconds,
                    attemptCount,
                    isStreaming,
                    upstreamHeaderMilliseconds,
                    timeToFirstByteMilliseconds,
                    tokenUsage?.InputTokens,
                    tokenUsage?.OutputTokens,
                    tokenUsage?.TotalTokens,
                    finalErrorCode));
            }
        }
    }

    private static void ObserveCompletedAttempt(
        IDeploymentPerformanceTracker performanceTracker,
        Guid deploymentId,
        bool infrastructureHealthy,
        Stopwatch requestStopwatch,
        long attemptStartedMilliseconds,
        long? requestTimeToFirstByteMilliseconds)
    {
        var durationMilliseconds = Math.Max(0, requestStopwatch.ElapsedMilliseconds - attemptStartedMilliseconds);
        var backendTimeToFirstByteMilliseconds = requestTimeToFirstByteMilliseconds is long timeToFirstByte
            ? Math.Max(0, timeToFirstByte - attemptStartedMilliseconds)
            : (long?)null;

        performanceTracker.Observe(
            deploymentId,
            infrastructureHealthy,
            durationMilliseconds,
            backendTimeToFirstByteMilliseconds,
            DateTimeOffset.UtcNow);
    }

    private static async Task HandleCapacityLeaseLostAsync(HttpContext context, Guid requestId)
    {
        if (Activity.Current is { } activity)
        {
            activity.AddEvent(new ActivityEvent("capacity_lease_lost"));
            activity.SetTag("llmproxy.capacity.result", "lease_lost");
            activity.SetTag("error.type", "capacity_lease_lost");
            activity.SetStatus(ActivityStatusCode.Error, "capacity_lease_lost");
        }

        using (var leaseLossActivity = LlmProxy.Application.Observability.LlmProxyActivity.Start("llmproxy.capacity.lease_lost"))
        {
            leaseLossActivity?.SetTag("llmproxy.capacity.result", "lease_lost");
            leaseLossActivity?.SetTag("llmproxy.request.id", requestId.ToString());
            LlmProxy.Application.Observability.LlmProxyActivity.MarkError(leaseLossActivity, "capacity_lease_lost");
        }

        if (context.Response.HasStarted)
        {
            context.Items[LlmProxy.Api.Observability.InferenceContentLoggingMiddleware.StreamFailureItem] = "capacity_lease_lost";
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
        if (Activity.Current is { TraceId: var traceId } && traceId != default)
        {
            context.Response.Headers["X-LlmProxy-Trace-Id"] = traceId.ToString();
        }

        context.Response.Headers.RetryAfter = "1";
        await WriteGatewayErrorAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "gateway_unavailable",
            "capacity_lease_lost",
            "Distributed inference capacity lease safety was lost while the request was running. Retry shortly.");
    }

    private static HttpRequestMessage CreateOutboundRequest(
        HttpRequest source,
        JsonObject requestObject,
        RouteSelection route,
        string upstreamPath,
        Guid requestId,
        UpstreamCredentialProtector upstreamCredentialProtector)
    {
        var destination = new HttpRequestMessage(
            HttpMethod.Post,
            InferenceEndpoint.Combine(route.BaseAddress, upstreamPath))
        {
            Content = new StringContent(requestObject.ToJsonString(), Encoding.UTF8, "application/json")
        };

        CopyRequestHeaders(source, destination);
        upstreamCredentialProtector.ApplyBearer(destination, route.UpstreamBearerTokenCiphertext);
        destination.Headers.Remove("X-LlmProxy-Request-Id");
        destination.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());
        return destination;
    }

    private static async Task CopyUpstreamBodyAsync(
        HttpResponseMessage upstream,
        HttpContext context,
        OpenAiResponseObserver observer,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        if (observer.IsStreaming)
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.StartAsync(cancellationToken);
        }

        await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (true)
            {
                int read;
                idle.CancelAfter(idleTimeout);
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Reported as a transport failure so the caller aborts the stream or answers 502 before the first byte.
                    throw new IOException("The inference runtime stopped sending data.");
                }

                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (read == 0)
                {
                    break;
                }

                observer.Observe(buffer.AsSpan(0, read));
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (observer.IsStreaming)
                {
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);

    // Caller credentials (API key, browser session cookies, proxy credentials) and
    // hop-by-hop headers must never reach an inference node.
    private static readonly HashSet<string> NonForwardedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Host", "Content-Length", "Transfer-Encoding",
        "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Upgrade", "Expect", "Content-Type",
        "Content-Encoding", "Content-MD5"
    };

    // Hop-by-hop headers and the upstream server banner are not part of the gateway's response.
    private static readonly HashSet<string> NonForwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Connection", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Upgrade", "Server"
    };

    public static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (var header in source.Headers)
        {
            if (NonForwardedRequestHeaders.Contains(header.Key)) continue;

            if (!destination.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                destination.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers)
        {
            if (!NonForwardedResponseHeaders.Contains(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in source.Content.Headers)
        {
            if (!NonForwardedResponseHeaders.Contains(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        destination.Headers.Remove("transfer-encoding");
    }

    private static Task WriteGatewayErrorAsync(HttpContext context, int statusCode, string type, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type,
                code
            }
        });
    }
}

internal static class OpenAiRequestPayload
{
    public static bool TryParse(
        string rawBody,
        out JsonObject requestObject,
        out string logicalModelName,
        out string errorCode,
        out string errorMessage)
    {
        requestObject = null!;
        logicalModelName = string.Empty;
        errorCode = string.Empty;
        errorMessage = string.Empty;

        try
        {
            requestObject = JsonNode.Parse(rawBody) as JsonObject ?? null!;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or FormatException)
        {
            errorCode = "invalid_json";
            errorMessage = "Request body is not valid JSON.";
            return false;
        }

        if (requestObject is null)
        {
            errorCode = "invalid_json";
            errorMessage = "Request body must be a JSON object.";
            return false;
        }

        if (requestObject["model"] is not JsonValue modelValue ||
            !modelValue.TryGetValue<string>(out var parsedModelName) ||
            string.IsNullOrWhiteSpace(parsedModelName))
        {
            errorCode = "model_required";
            errorMessage = "A logical model name is required.";
            return false;
        }

        if (!LogicalModelName.IsAcceptable(parsedModelName))
        {
            errorCode = "model_name_too_long";
            errorMessage = $"The model name must be at most {LogicalModelName.MaxLength} characters.";
            return false;
        }

        logicalModelName = parsedModelName;
        return true;
    }

    public static void RewriteModel(JsonObject requestObject, string providerModelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerModelName);
        requestObject["model"] = providerModelName;
    }
}