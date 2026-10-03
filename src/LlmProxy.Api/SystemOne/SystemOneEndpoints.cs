using System.Diagnostics;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.SystemOne;

public static class SystemOneEndpoints
{
    private const string SystemOnePath = "/v1/systemone";
    private const int MaxUpstreamAttempts = 3;
    public const string LogicalModelItem = "LlmProxy.SystemOne.LogicalModel";

    public static IEndpointRouteBuilder MapSystemOneEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/systemone/models", async (IDeploymentCatalog catalog, CancellationToken cancellationToken) =>
        {
            var models = await catalog.GetPublicModelsAsync(cancellationToken, ModelSurface.SystemOne);
            return Results.Ok(new
            {
                @object = "list",
                data = models.Select(model => new
                {
                    id = model.PublicName,
                    @object = "model",
                    surface = "systemone",
                    owned_by = "llmproxy"
                })
            });
        });
        endpoints.MapPost(SystemOnePath, ForwardAsync);
        return endpoints;
    }

    private static async Task ForwardAsync(
        HttpContext context,
        IConfiguration configuration,
        IDeploymentCatalog catalog,
        RoutingService routingService,
        IRequestCapacityGate capacityGate,
        RequestRateLimiter rateLimiter,
        IRequestMetricsSink metricsSink,
        IHttpClientFactory httpClientFactory,
        UpstreamCredentialProtector upstreamCredentialProtector)
    {
        var requestId = Guid.NewGuid();
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var apiCredentialId = context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var credentialValue) && credentialValue is Guid credentialId
            ? credentialId
            : (Guid?)null;
        var usageGroupId = context.Items.TryGetValue(InferenceApiKeyMiddleware.UsageGroupIdItem, out var groupValue) && groupValue is Guid groupId
            ? groupId
            : (Guid?)null;
        var ownerTenantId = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerTenantIdItem, out var tenantValue) ? tenantValue as string : null;
        var ownerObjectId = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerObjectIdItem, out var ownerValue) ? ownerValue as string : null;
        var enforceCallerGovernance =
            context.Items.TryGetValue(InferenceApiKeyMiddleware.EnforceCallerGovernanceItem, out var governanceValue) &&
            governanceValue is true;

        Guid? finalDeploymentId = null;
        Guid? finalNodeId = null;
        var finalStatusCode = StatusCodes.Status500InternalServerError;
        var attemptCount = 0;
        long? upstreamHeaderMilliseconds = null;
        string? finalErrorCode = null;
        string logicalModel = string.Empty;
        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();

        try
        {
            var models = await catalog.GetPublicModelsAsync(context.RequestAborted, ModelSurface.SystemOne);
            var requested = context.Request.Headers["X-LlmProxy-Model"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(requested))
            {
                requested = configuration["SystemOne:DefaultModel"];
            }

            if (string.IsNullOrWhiteSpace(requested))
            {
                if (models.Count == 1)
                {
                    requested = models[0].PublicName;
                }
                else if (models.Count == 0)
                {
                    finalStatusCode = StatusCodes.Status503ServiceUnavailable;
                    finalErrorCode = "classifier_unavailable";
                    await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, "No enabled System One logical model is configured.");
                    return;
                }
                else
                {
                    finalStatusCode = StatusCodes.Status400BadRequest;
                    finalErrorCode = "systemone_model_required";
                    await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, "Multiple System One models are enabled. Send X-LlmProxy-Model with the logical model name.");
                    return;
                }
            }

            logicalModel = requested.Trim();
            if (!models.Any(model => string.Equals(model.PublicName, logicalModel, StringComparison.Ordinal)))
            {
                finalStatusCode = StatusCodes.Status404NotFound;
                finalErrorCode = "systemone_model_not_found";
                await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, $"System One logical model '{logicalModel}' is not enabled.");
                return;
            }
            context.Items[LogicalModelItem] = logicalModel;

            if (apiCredentialId is Guid callerCredentialId)
            {
                var decision = await rateLimiter.TryAcquireAsync(
                    callerCredentialId,
                    logicalModel,
                    ownerTenantId,
                    ownerObjectId,
                    usageGroupId,
                    enforceCallerGovernance,
                    DateTimeOffset.UtcNow,
                    context.RequestAborted);
                if (!decision.Allowed)
                {
                    finalStatusCode = StatusCodes.Status429TooManyRequests;
                    finalErrorCode = "rate_limit_exceeded";
                    context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, $"Request rate limit exceeded for model '{logicalModel}'.");
                    return;
                }
            }

            byte[] body;
            await using (var buffer = new MemoryStream())
            {
                await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
                body = buffer.ToArray();
            }

            var excluded = new HashSet<Guid>();
            var client = httpClientFactory.CreateClient("system-one");
            for (var attempt = 1; attempt <= MaxUpstreamAttempts; attempt++)
            {
                var selection = await routingService.SelectDetailedAsync(
                    logicalModel,
                    ModelSurface.SystemOne,
                    excluded,
                    context.RequestAborted);
                var route = selection.Route;
                if (route is null)
                {
                    finalStatusCode = selection.Failure == RoutingSelectionFailure.CapacityExhausted
                        ? StatusCodes.Status429TooManyRequests
                        : StatusCodes.Status503ServiceUnavailable;
                    finalErrorCode = selection.Failure == RoutingSelectionFailure.CapacityExhausted
                        ? "capacity_exhausted"
                        : "no_healthy_deployment";
                    await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, $"No available System One deployment exists for '{logicalModel}'.");
                    return;
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
                        finalStatusCode = StatusCodes.Status503ServiceUnavailable;
                        finalErrorCode = "capacity_coordination_unavailable";
                        await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, "Distributed capacity coordination is unavailable.");
                        return;
                    }

                    excluded.Add(route.DeploymentId);
                    attempt--;
                    continue;
                }

                await using var lease = admission.Lease;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.CoordinationLost);
                attemptCount++;
                finalDeploymentId = route.DeploymentId;
                finalNodeId = route.NodeId;

                using var outbound = new HttpRequestMessage(HttpMethod.Post, InferenceEndpoint.Combine(route.BaseAddress, SystemOnePath))
                {
                    Content = new ByteArrayContent(body)
                };
                if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                {
                    outbound.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }
                if (context.Request.Headers.TryGetValue("Accept", out var accept))
                {
                    outbound.Headers.TryAddWithoutValidation("Accept", accept.ToArray());
                }
                outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());
                upstreamCredentialProtector.ApplyBearer(outbound, route.UpstreamBearerTokenCiphertext);

                HttpResponseMessage upstream;
                var headerWatch = Stopwatch.StartNew();
                try
                {
                    upstream = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                    upstreamHeaderMilliseconds = headerWatch.ElapsedMilliseconds;
                }
                catch (OperationCanceledException) when (lease.CoordinationLost.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
                {
                    excluded.Add(route.DeploymentId);
                    if (attempt < MaxUpstreamAttempts) continue;
                    finalStatusCode = StatusCodes.Status503ServiceUnavailable;
                    finalErrorCode = "capacity_lease_lost";
                    await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, "Capacity coordination was lost while calling the System One deployment.");
                    return;
                }
                catch (HttpRequestException)
                {
                    excluded.Add(route.DeploymentId);
                    if (attempt < MaxUpstreamAttempts) continue;
                    finalStatusCode = StatusCodes.Status502BadGateway;
                    finalErrorCode = "classifier_unreachable";
                    await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, "All eligible System One deployments were unreachable.");
                    return;
                }

                using (upstream)
                {
                    if ((int)upstream.StatusCode >= 500 && attempt < MaxUpstreamAttempts)
                    {
                        excluded.Add(route.DeploymentId);
                        continue;
                    }

                    finalStatusCode = (int)upstream.StatusCode;
                    context.Response.StatusCode = finalStatusCode;
                    CopyResponseHeaders(upstream, context.Response);
                    await using var responseBody = await upstream.Content.ReadAsStreamAsync(linked.Token);
                    await responseBody.CopyToAsync(context.Response.Body, linked.Token);
                    return;
                }
            }

            finalStatusCode = StatusCodes.Status503ServiceUnavailable;
            finalErrorCode = "no_healthy_deployment";
            await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, $"No available System One deployment exists for '{logicalModel}'.");
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                finalStatusCode = StatusCodes.Status504GatewayTimeout;
                finalErrorCode = "classifier_timeout";
                context.Response.Clear();
                context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
                await WriteGatewayErrorAsync(context, finalStatusCode, finalErrorCode, "The selected System One deployment timed out.");
            }
        }
        finally
        {
            metricsSink.Write(new GatewayRequestMetric(
                requestId,
                startedAtUtc,
                string.IsNullOrWhiteSpace(logicalModel) ? "systemone" : logicalModel,
                "systemone",
                finalDeploymentId,
                finalNodeId,
                apiCredentialId,
                usageGroupId,
                finalStatusCode,
                stopwatch.ElapsedMilliseconds,
                attemptCount,
                false,
                upstreamHeaderMilliseconds,
                null,
                null,
                null,
                null,
                finalErrorCode));
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers)
        {
            if (!IsHopByHopHeader(header.Key)) destination.Headers[header.Key] = header.Value.ToArray();
        }
        foreach (var header in source.Content.Headers)
        {
            if (!IsHopByHopHeader(header.Key)) destination.Headers[header.Key] = header.Value.ToArray();
        }
        destination.Headers.Remove("transfer-encoding");
    }

    private static bool IsHopByHopHeader(string name) =>
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static Task WriteGatewayErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error = new { message, type = "gateway_error", code } });
    }
}
