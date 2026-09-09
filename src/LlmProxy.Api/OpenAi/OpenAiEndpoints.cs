using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;

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
        IRequestLoadTracker loadTracker,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory) =>
        ForwardInferenceAsync(
            context,
            routingService,
            loadTracker,
            metricsSink,
            httpClientFactory,
            "/v1/chat/completions");

    private static Task ForwardResponsesAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestLoadTracker loadTracker,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory) =>
        ForwardInferenceAsync(
            context,
            routingService,
            loadTracker,
            metricsSink,
            httpClientFactory,
            "/v1/responses");

    private static async Task ForwardInferenceAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestLoadTracker loadTracker,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory,
        string upstreamPath)
    {
        var requestId = Guid.NewGuid();
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        Guid? finalDeploymentId = null;
        Guid? finalNodeId = null;
        var finalStatusCode = StatusCodes.Status500InternalServerError;
        string? finalErrorCode = null;
        string? publicModelName = null;

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

            var excluded = new HashSet<Guid>();
            var client = httpClientFactory.CreateClient("vllm");

            for (var attempt = 1; attempt <= MaxUpstreamAttempts; attempt++)
            {
                var route = await routingService.SelectAsync(publicModelName, excluded, context.RequestAborted);
                if (route is null)
                {
                    break;
                }

                finalDeploymentId = route.DeploymentId;
                finalNodeId = route.NodeId;
                OpenAiRequestPayload.RewriteModel(requestObject, route.ProviderModelName);

                using var lease = loadTracker.Enter(route.DeploymentId);
                using var outbound = CreateOutboundRequest(context.Request, requestObject, route, upstreamPath);

                try
                {
                    using var upstream = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    finalStatusCode = (int)upstream.StatusCode;

                    if ((int)upstream.StatusCode >= 500 && attempt < MaxUpstreamAttempts)
                    {
                        excluded.Add(route.DeploymentId);
                        finalErrorCode = "upstream_server_error";
                        continue;
                    }

                    context.Response.StatusCode = finalStatusCode;
                    CopyResponseHeaders(upstream, context.Response);
                    await upstream.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                    finalErrorCode = upstream.IsSuccessStatusCode ? null : "upstream_error";
                    return;
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    finalStatusCode = 499;
                    finalErrorCode = "client_cancelled";
                    return;
                }
                catch (HttpRequestException) when (attempt < MaxUpstreamAttempts)
                {
                    excluded.Add(route.DeploymentId);
                    finalStatusCode = StatusCodes.Status502BadGateway;
                    finalErrorCode = "upstream_unreachable";
                }
                catch (HttpRequestException exception)
                {
                    finalStatusCode = StatusCodes.Status502BadGateway;
                    finalErrorCode = "upstream_unreachable";
                    if (!context.Response.HasStarted)
                    {
                        await WriteGatewayErrorAsync(
                            context,
                            finalStatusCode,
                            "gateway_error",
                            finalErrorCode,
                            $"Inference runtime '{route.NodeName}' could not be reached: {exception.Message}");
                    }
                    return;
                }
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
                var apiCredentialId = context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var value) && value is Guid id
                    ? id
                    : (Guid?)null;

                metricsSink.Write(new GatewayRequestMetric(
                    requestId,
                    startedAtUtc,
                    publicModelName,
                    finalDeploymentId,
                    finalNodeId,
                    apiCredentialId,
                    finalStatusCode,
                    stopwatch.ElapsedMilliseconds,
                    finalErrorCode));
            }
        }
    }

    private static HttpRequestMessage CreateOutboundRequest(
        HttpRequest source,
        JsonObject requestObject,
        RouteSelection route,
        string upstreamPath)
    {
        var destination = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{route.BaseAddress.TrimEnd('/')}{upstreamPath}"))
        {
            Content = new StringContent(requestObject.ToJsonString(), Encoding.UTF8, "application/json")
        };

        CopyRequestHeaders(source, destination);
        return destination;
    }

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
            !modelValue.TryGetValue<string>(out logicalModelName) ||
            string.IsNullOrWhiteSpace(logicalModelName))
        {
            logicalModelName = string.Empty;
            errorCode = "model_required";
            errorMessage = "A logical model name is required.";
            return false;
        }

        return true;
    }

    public static void RewriteModel(JsonObject requestObject, string providerModelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerModelName);
        requestObject["model"] = providerModelName;
    }
}
