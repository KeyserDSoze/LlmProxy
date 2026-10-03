using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

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

        group.MapGet("/systemone", async (
            IDeploymentCatalog catalog,
            GatewayDbContext dbContext,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var models = await catalog.GetPublicModelsAsync(cancellationToken, ModelSurface.SystemOne);
            var modelIds = await dbContext.Models.AsNoTracking()
                .Where(item => item.Enabled && item.Surface == ModelSurface.SystemOne)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken);
            var topology = await dbContext.Deployments.AsNoTracking()
                .Where(item => item.Enabled && modelIds.Contains(item.ModelId))
                .Join(
                    dbContext.Nodes.AsNoTracking().Where(item => item.Enabled),
                    deployment => deployment.NodeId,
                    node => node.Id,
                    (deployment, node) => new
                    {
                        deployment.RuntimeBaseAddress,
                        node.BaseAddress,
                        node.UpstreamBearerTokenCiphertext
                    })
                .FirstOrDefaultAsync(cancellationToken);

            var baseAddress = topology is null
                ? null
                : string.IsNullOrWhiteSpace(topology.RuntimeBaseAddress) ? topology.BaseAddress : topology.RuntimeBaseAddress;
            string? upstreamEndpoint = null;
            if (!string.IsNullOrWhiteSpace(baseAddress))
            {
                upstreamEndpoint = InferenceEndpoint.Combine(baseAddress, SystemOnePath).ToString();
            }

            return Results.Ok(new
            {
                enabled = models.Count > 0,
                baseAddress,
                upstreamEndpoint,
                publicEndpoint = SystemOnePath,
                apiKeyConfigured = !string.IsNullOrWhiteSpace(topology?.UpstreamBearerTokenCiphertext),
                timeoutSeconds = Math.Clamp(configuration.GetValue<int?>("SystemOne:TimeoutSeconds") ?? 30, 1, 300),
                configurationError = models.Count == 0 ? "No enabled System One logical model is deployed." : null,
                models = models.Select(item => item.PublicName).ToArray(),
                defaultModel = string.IsNullOrWhiteSpace(configuration["SystemOne:DefaultModel"])
                    ? (models.Count == 1 ? models[0].PublicName : null)
                    : configuration["SystemOne:DefaultModel"]!.Trim()
            });
        });

        group.MapPost("/systemone", async (
            SystemOneTestRequest request,
            IDeploymentCatalog catalog,
            RoutingService routingService,
            IRequestCapacityGate capacityGate,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector upstreamCredentialProtector,
            IInferenceContentLogSink contentLogSink,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var models = await catalog.GetPublicModelsAsync(cancellationToken, ModelSurface.SystemOne);
            var logicalModel = string.IsNullOrWhiteSpace(request.Model) ? configuration["SystemOne:DefaultModel"] : request.Model.Trim();
            if (string.IsNullOrWhiteSpace(logicalModel) && models.Count == 1) logicalModel = models[0].PublicName;
            if (string.IsNullOrWhiteSpace(logicalModel) || !models.Any(item => string.Equals(item.PublicName, logicalModel, StringComparison.Ordinal)))
            {
                return Results.Json(new
                {
                    error = models.Count == 0 ? "classifier_unavailable" : "systemone_model_required",
                    message = models.Count == 0 ? "No enabled System One logical model is deployed." : "Select a System One logical model before running the diagnostic."
                }, statusCode: models.Count == 0 ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status400BadRequest);
            }

            var requestId = Guid.NewGuid();
            var startedAtUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var requestBody = request.Payload.GetRawText();
            var statusCode = StatusCodes.Status502BadGateway;
            var responseBody = string.Empty;
            string? responseContentType = null;

            var selected = await routingService.SelectDetailedAsync(logicalModel, ModelSurface.SystemOne, null, cancellationToken);
            if (selected.Route is not { } route)
            {
                var unavailableStatus = selected.Failure == RoutingSelectionFailure.CapacityExhausted ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable;
                return Results.Json(new { error = selected.Failure == RoutingSelectionFailure.CapacityExhausted ? "capacity_exhausted" : "no_healthy_deployment", model = logicalModel }, statusCode: unavailableStatus);
            }

            var admission = await capacityGate.TryAcquireAsync(route.DeploymentId, route.NodeId, route.MaxConcurrency, route.NodeMaxConcurrency, cancellationToken);
            if (!admission.Acquired || admission.Lease is null)
            {
                return Results.Json(new { error = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable ? "capacity_coordination_unavailable" : "capacity_exhausted", model = logicalModel }, statusCode: admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable ? 503 : 429);
            }

            await using var lease = admission.Lease;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CoordinationLost);
            var destination = InferenceEndpoint.Combine(route.BaseAddress, SystemOnePath);
            try
            {
                using var outbound = new HttpRequestMessage(HttpMethod.Post, destination)
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
                outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());
                upstreamCredentialProtector.ApplyBearer(outbound, route.UpstreamBearerTokenCiphertext);

                using var upstream = await httpClientFactory.CreateClient("system-one").SendAsync(outbound, HttpCompletionOption.ResponseContentRead, linked.Token);
                statusCode = (int)upstream.StatusCode;
                responseContentType = upstream.Content.Headers.ContentType?.ToString();
                responseBody = await upstream.Content.ReadAsStringAsync(linked.Token);
                httpContext.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new
                {
                    requestId,
                    success = upstream.IsSuccessStatusCode,
                    statusCode,
                    latencyMilliseconds = stopwatch.ElapsedMilliseconds,
                    upstreamEndpoint = destination.ToString(),
                    logicalModel,
                    providerModel = route.ProviderModelName,
                    deploymentId = route.DeploymentId,
                    nodeId = route.NodeId,
                    nodeName = route.NodeName,
                    requestBody,
                    responseContentType,
                    responseBody
                });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                statusCode = StatusCodes.Status504GatewayTimeout;
                responseBody = """{"error":"classifier_timeout"}""";
                return Results.Json(new { requestId, success = false, statusCode, latencyMilliseconds = stopwatch.ElapsedMilliseconds, logicalModel, nodeName = route.NodeName, requestBody, responseBody, error = "classifier_timeout" }, statusCode: statusCode);
            }
            catch (HttpRequestException exception)
            {
                statusCode = StatusCodes.Status502BadGateway;
                responseBody = JsonSerializer.Serialize(new { error = "classifier_unreachable", message = exception.Message });
                return Results.Json(new { requestId, success = false, statusCode, latencyMilliseconds = stopwatch.ElapsedMilliseconds, logicalModel, nodeName = route.NodeName, requestBody, responseBody, error = "classifier_unreachable" }, statusCode: statusCode);
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
                    logicalModel,
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
            var routeResult = await routingService.SelectDetailedAsync(request.Model.Trim(), ModelSurface.OpenAi, null, cancellationToken);
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

    public sealed record SystemOneTestRequest(JsonElement Payload, string? Model = null);

    public sealed record ChatModelTestRequest(
        string Model,
        string UserPrompt,
        string? SystemPrompt = null,
        int? MaxTokens = 256,
        double? Temperature = 0.2);
}
