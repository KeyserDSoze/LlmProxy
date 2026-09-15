using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace LlmProxy.Api.OpenAi;

public static class OpenAiEndpoints
{
    private const int MaxUpstreamAttempts = 3;

    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", async (IDeploymentCatalog catalog, CancellationToken cancellationToken) =>
        {
            var models = await catalog.GetPublicModelsAsync(cancellationToken);
            return Results.Ok(new
            {
                @object = "list",
                data = models.Select(model => new
                {
                    id = model.PublicName,
                    @object = "model",
                    created = 0,
                    owned_by = "llmproxy"
                })
            });
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
        IHttpClientFactory httpClientFactory) =>
        ForwardInferenceAsync(
            context,
            routingService,
            capacityGate,
            performanceTracker,
            rateLimiter,
            metricsSink,
            httpClientFactory,
            "/v1/chat/completions");

    private static Task ForwardResponsesAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestCapacityGate capacityGate,
        IDeploymentPerformanceTracker performanceTracker,
        RequestRateLimiter rateLimiter,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory) =>
        ForwardInferenceAsync(
            context,
            routingService,
            capacityGate,
            performanceTracker,
            rateLimiter,
            metricsSink,
            httpClientFactory,
            "/v1/responses");

    private static async Task ForwardInferenceAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestCapacityGate capacityGate,
        IDeploymentPerformanceTracker performanceTracker,
        RequestRateLimiter rateLimiter,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory,
        string upstreamPath)
    {
        var requestId = Guid.NewGuid();
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
                var selection = await routingService.SelectDetailedAsync(publicModelName, excluded, context.RequestAborted);
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
                attemptCount++;
                finalDeploymentId = route.DeploymentId;
                finalNodeId = route.NodeId;
                OpenAiRequestPayload.RewriteModel(requestObject, route.ProviderModelName);

                using var outbound = CreateOutboundRequest(context.Request, requestObject, route, upstreamPath, requestId);
                HttpResponseMessage upstream;
                var attemptStartedMilliseconds = stopwatch.ElapsedMilliseconds;
                var upstreamStopwatch = Stopwatch.StartNew();

                try
                {
                    upstream = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    upstreamHeaderMilliseconds = upstreamStopwatch.ElapsedMilliseconds;
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    finalStatusCode = 499;
                    finalErrorCode = "client_cancelled";
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

                    await WriteGatewayErrorAsync(
                        context,
                        finalStatusCode,
                        "gateway_error",
                        finalErrorCode,
                        $"Inference runtime '{route.NodeName}' could not be reached: {exception.Message}");
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
                        await CopyUpstreamBodyAsync(upstream, context, observer);
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
                            context.Abort();
                            return;
                        }

                        finalStatusCode = StatusCodes.Status502BadGateway;
                        await WriteGatewayErrorAsync(
                            context,
                            finalStatusCode,
                            "gateway_error",
                            finalErrorCode,
                            $"Inference stream from '{route.NodeName}' was interrupted: {exception.Message}");
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

            if (routingFailure == RoutingSelectionFailure.CapacityExhausted || capacityRaceObserved)
            {
                finalStatusCode = StatusCodes.Status429TooManyRequests;
                finalErrorCode = "capacity_exhausted";
                context.Response.Headers.RetryAfter = "1";
                await WriteGatewayErrorAsync(
                    context,
                    finalStatusCode,
                    "rate_limit_error",
                    finalErrorCode,
                    $"Inference capacity is temporarily exhausted for model '{publicModelName}'. Retry shortly.");
                return;
            }

            finalStatusCode = StatusCodes.Status503ServiceUnavailable;
            finalErrorCode = "no_healthy_deployment";
            await WriteGatewayErrorAsync(
                context,
                finalStatusCode,
                "gateway_unavailable",
                finalErrorCode,
                $"No healthy deployment is available for model '{publicModelName}'.");
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

    private static HttpRequestMessage CreateOutboundRequest(
        HttpRequest source,
        JsonObject requestObject,
        RouteSelection route,
        string upstreamPath,
        Guid requestId)
    {
        var destination = new HttpRequestMessage(
            HttpMethod.Post,
            InferenceEndpoint.Combine(route.BaseAddress, upstreamPath))
        {
            Content = new StringContent(requestObject.ToJsonString(), Encoding.UTF8, "application/json")
        };

        CopyRequestHeaders(source, destination);
        destination.Headers.Remove("X-LlmProxy-Request-Id");
        destination.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());
        return destination;
    }

    private static async Task CopyUpstreamBodyAsync(
        HttpResponseMessage upstream,
        HttpContext context,
        OpenAiResponseObserver observer)
    {
        if (observer.IsStreaming)
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.StartAsync(context.RequestAborted);
        }

        await using var source = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestAborted);
                if (read == 0)
                {
                    break;
                }

                observer.Observe(buffer.AsSpan(0, read));
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                if (observer.IsStreaming)
                {
                    await context.Response.Body.FlushAsync(context.RequestAborted);
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

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (var header in source.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
            if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in source.Content.Headers)
        {
            if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
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

        logicalModelName = parsedModelName;
        return true;
    }

    public static void RewriteModel(JsonObject requestObject, string providerModelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerModelName);
        requestObject["model"] = providerModelName;
    }
}
