using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.Admin;

public static class TestingAdminEndpoints
{
    private const string SystemOnePath = "/v1/systemone";

    public static IEndpointRouteBuilder MapTestingAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/testing");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminWrite");
        }

        group.MapGet("/systemone", (
            IConfiguration configuration) =>
        {
            var enabled = configuration.GetValue<bool>("SystemOne:Enabled");
            var baseAddress = configuration["SystemOne:BaseAddress"];
            var timeoutSeconds = Math.Clamp(configuration.GetValue<int?>("SystemOne:TimeoutSeconds") ?? 30, 1, 300);
            string? upstreamEndpoint = null;
            string? configurationError = null;

            if (!string.IsNullOrWhiteSpace(baseAddress))
            {
                try
                {
                    upstreamEndpoint = InferenceEndpoint.Combine(baseAddress, SystemOnePath).ToString();
                }
                catch (ArgumentException exception)
                {
                    configurationError = exception.Message;
                }
            }

            return Results.Ok(new
            {
                enabled,
                baseAddress,
                upstreamEndpoint,
                publicEndpoint = SystemOnePath,
                apiKeyConfigured = !string.IsNullOrWhiteSpace(configuration["SystemOne:ApiKey"]),
                timeoutSeconds,
                configurationError
            });
        });

        group.MapPost("/systemone", async (
            SystemOneTestRequest request,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            IInferenceContentLogSink contentLogSink,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!configuration.GetValue<bool>("SystemOne:Enabled"))
            {
                return Results.Json(new
                {
                    error = "classifier_unavailable",
                    message = "System One is disabled. Enable SystemOne:Enabled before testing the classifier."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var baseAddress = configuration["SystemOne:BaseAddress"];
            if (string.IsNullOrWhiteSpace(baseAddress))
            {
                return Results.Json(new
                {
                    error = "classifier_not_configured",
                    message = "SystemOne:BaseAddress is not configured."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            Uri destination;
            try
            {
                destination = InferenceEndpoint.Combine(baseAddress, SystemOnePath);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "classifier_address_invalid", message = exception.Message });
            }

            var requestId = Guid.NewGuid();
            var startedAtUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var requestBody = request.Payload.GetRawText();
            var statusCode = StatusCodes.Status502BadGateway;
            var responseBody = string.Empty;
            string? responseContentType = null;

            try
            {
                using var outbound = new HttpRequestMessage(HttpMethod.Post, destination)
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
                outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());

                var upstreamApiKey = configuration["SystemOne:ApiKey"];
                if (!string.IsNullOrWhiteSpace(upstreamApiKey))
                {
                    outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstreamApiKey.Trim());
                }

                using var upstream = await httpClientFactory.CreateClient("system-one").SendAsync(
                    outbound,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken);
                statusCode = (int)upstream.StatusCode;
                responseContentType = upstream.Content.Headers.ContentType?.ToString();
                responseBody = await upstream.Content.ReadAsStringAsync(cancellationToken);

                httpContext.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new
                {
                    requestId,
                    success = upstream.IsSuccessStatusCode,
                    statusCode,
                    latencyMilliseconds = stopwatch.ElapsedMilliseconds,
                    upstreamEndpoint = destination.ToString(),
                    requestBody,
                    responseContentType,
                    responseBody
                });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                statusCode = StatusCodes.Status504GatewayTimeout;
                responseBody = """{"error":"classifier_timeout"}""";
                return Results.Json(new
                {
                    requestId,
                    success = false,
                    statusCode,
                    latencyMilliseconds = stopwatch.ElapsedMilliseconds,
                    upstreamEndpoint = destination.ToString(),
                    requestBody,
                    responseBody,
                    error = "classifier_timeout"
                }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (HttpRequestException exception)
            {
                statusCode = StatusCodes.Status502BadGateway;
                responseBody = JsonSerializer.Serialize(new { error = "classifier_unreachable", message = exception.Message });
                return Results.Json(new
                {
                    requestId,
                    success = false,
                    statusCode,
                    latencyMilliseconds = stopwatch.ElapsedMilliseconds,
                    upstreamEndpoint = destination.ToString(),
                    requestBody,
                    responseBody,
                    error = "classifier_unreachable"
                }, statusCode: StatusCodes.Status502BadGateway);
            }
            finally
            {
                contentLogSink.Write(new InferenceContentLog(
                    requestId,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    "systemone_test",
                    HttpMethods.Post,
                    "/api/admin/testing/systemone",
                    null,
                    null,
                    statusCode,
                    "application/json",
                    responseContentType ?? "application/json",
                    requestBody,
                    responseBody));
            }
        });

        group.MapPost("/chat", async (
            ChatModelTestRequest request,
            RoutingService routingService,
            IRequestCapacityGate capacityGate,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector upstreamCredentialProtector,
            IInferenceContentLogSink contentLogSink,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.UserPrompt))
            {
                return Results.BadRequest(new { error = "model_and_user_prompt_required" });
            }

            var requestId = Guid.NewGuid();
            var startedAtUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var maxTokens = Math.Clamp(request.MaxTokens ?? 256, 1, 8192);
            var temperature = Math.Clamp(request.Temperature ?? 0.2, 0, 2);
            var routeResult = await routingService.SelectDetailedAsync(request.Model.Trim(), null, cancellationToken);
            if (routeResult.Route is not { } route)
            {
                var code = routeResult.Failure == RoutingSelectionFailure.CapacityExhausted
                    ? "capacity_exhausted"
                    : "no_healthy_deployment";
                var status = routeResult.Failure == RoutingSelectionFailure.CapacityExhausted
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status503ServiceUnavailable;
                return Results.Json(new { error = code, model = request.Model }, statusCode: status);
            }

            var admission = await capacityGate.TryAcquireAsync(
                route.DeploymentId,
                route.NodeId,
                route.MaxConcurrency,
                route.NodeMaxConcurrency,
                cancellationToken);
            if (!admission.Acquired || admission.Lease is null)
            {
                var status = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status429TooManyRequests;
                var code = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable
                    ? "capacity_coordination_unavailable"
                    : "capacity_exhausted";
                return Results.Json(new { error = code, model = request.Model, node = route.NodeName }, statusCode: status);
            }

            await using var lease = admission.Lease;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lease.CoordinationLost);

            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            {
                messages.Add(new { role = "system", content = request.SystemPrompt });
            }
            messages.Add(new { role = "user", content = request.UserPrompt });

            var payload = JsonSerializer.Serialize(new
            {
                model = route.ProviderModelName,
                messages,
                stream = false,
                max_tokens = maxTokens,
                temperature
            });
            var statusCode = StatusCodes.Status502BadGateway;
            var responseBody = string.Empty;
            string? responseContentType = null;

            try
            {
                using var outbound = new HttpRequestMessage(
                    HttpMethod.Post,
                    InferenceEndpoint.Combine(route.BaseAddress, "/v1/chat/completions"))
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());
                upstreamCredentialProtector.ApplyBearer(outbound, route.UpstreamBearerTokenCiphertext);

                using var upstream = await httpClientFactory.CreateClient("vllm").SendAsync(
                    outbound,
                    HttpCompletionOption.ResponseContentRead,
                    linkedCancellation.Token);
                statusCode = (int)upstream.StatusCode;
                responseContentType = upstream.Content.Headers.ContentType?.ToString();
                responseBody = await upstream.Content.ReadAsStringAsync(linkedCancellation.Token);

                httpContext.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new
                {
                    requestId,
                    success = upstream.IsSuccessStatusCode,
                    statusCode,
                    latencyMilliseconds = stopwatch.ElapsedMilliseconds,
                    logicalModel = request.Model,
                    providerModel = route.ProviderModelName,
                    deploymentId = route.DeploymentId,
                    nodeId = route.NodeId,
                    nodeName = route.NodeName,
                    requestBody = payload,
                    responseContentType,
                    responseBody
                });
            }
            catch (OperationCanceledException) when (lease.CoordinationLost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                statusCode = StatusCodes.Status503ServiceUnavailable;
                responseBody = """{"error":"capacity_lease_lost"}""";
                return Results.Json(new
                {
                    requestId,
                    success = false,
                    statusCode,
                    latencyMilliseconds = stopwatch.ElapsedMilliseconds,
                    logicalModel = request.Model,
                    nodeName = route.NodeName,
                    error = "capacity_lease_lost"
                }, statusCode: statusCode);
            }
            catch (HttpRequestException exception)
            {
                statusCode = StatusCodes.Status502BadGateway;
                responseBody = JsonSerializer.Serialize(new { error = "upstream_unreachable", message = exception.Message });
                return Results.Json(new
                {
                    requestId,
                    success = false,
                    statusCode,
                    latencyMilliseconds = stopwatch.ElapsedMilliseconds,
                    logicalModel = request.Model,
                    nodeName = route.NodeName,
                    error = "upstream_unreachable",
                    message = exception.Message
                }, statusCode: statusCode);
            }
            finally
            {
                contentLogSink.Write(new InferenceContentLog(
                    requestId,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    "model_test",
                    HttpMethods.Post,
                    "/api/admin/testing/chat",
                    request.Model.Trim(),
                    null,
                    statusCode,
                    "application/json",
                    responseContentType ?? "application/json",
                    payload,
                    responseBody));
            }
        });

        return endpoints;
    }

    public sealed record SystemOneTestRequest(JsonElement Payload);

    public sealed record ChatModelTestRequest(
        string Model,
        string UserPrompt,
        string? SystemPrompt = null,
        int? MaxTokens = 256,
        double? Temperature = 0.2);
}
