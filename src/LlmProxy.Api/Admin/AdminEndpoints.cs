using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("/overview", async (GatewayDbContext dbContext, IRequestLoadTracker tracker, CancellationToken cancellationToken) =>
        {
            var nodes = await dbContext.Nodes.AsNoTracking().OrderBy(node => node.Name).ToListAsync(cancellationToken);
            var deployments = await dbContext.Deployments.AsNoTracking().ToListAsync(cancellationToken);
            var models = await dbContext.Models.AsNoTracking().ToListAsync(cancellationToken);
            var today = DateTimeOffset.UtcNow.Date;
            var requestsToday = await dbContext.RequestMetrics.CountAsync(metric => metric.StartedAtUtc >= today, cancellationToken);

            return Results.Ok(new
            {
                nodes = new
                {
                    total = nodes.Count,
                    healthy = nodes.Count(node => node.Status == NodeStatus.Healthy),
                    degraded = nodes.Count(node => node.Status == NodeStatus.Degraded),
                    unhealthy = nodes.Count(node => node.Status == NodeStatus.Unhealthy),
                    draining = nodes.Count(node => node.Status == NodeStatus.Draining)
                },
                models = models.Count(model => model.Enabled),
                deployments = deployments.Count(deployment => deployment.Enabled),
                activeRequests = deployments.Sum(deployment => tracker.GetActive(deployment.Id)),
                requestsToday
            });
        });

        group.MapGet("/routing", (RoutingStrategyState strategyState) => Results.Ok(new
        {
            strategy = strategyState.Current,
            supportedStrategies = Enum.GetValues<RoutingStrategy>()
        }));

        var updateRouting = group.MapPut("/routing", async (
            UpdateRoutingRequest request,
            GatewayDbContext dbContext,
            RoutingStrategyState strategyState,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.RoutingPolicies.SingleAsync(
                item => item.Id == RoutingPolicy.SingletonId,
                cancellationToken);
            var previous = policy.Strategy;
            policy.SetStrategy(request.Strategy);
            AddAudit(dbContext, httpContext, "routing.update", "routing_policy", policy.Id.ToString(), new
            {
                previous,
                current = policy.Strategy
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            strategyState.Set(policy.Strategy);

            return Results.Ok(new
            {
                strategy = strategyState.Current,
                supportedStrategies = Enum.GetValues<RoutingStrategy>(),
                policy.UpdatedAtUtc
            });
        });

        group.MapGet("/nodes", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await dbContext.Nodes.AsNoTracking().OrderBy(node => node.Name).ToListAsync(cancellationToken)));

        var createNode = group.MapPost("/nodes", async (
            CreateNodeRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = new InferenceNode(request.Name, request.BaseAddress, request.Weight, request.MaxConcurrency);
            dbContext.Nodes.Add(node);
            AddAudit(dbContext, httpContext, "node.create", "node", node.Id.ToString(), new
            {
                node.Name,
                node.BaseAddress,
                node.Weight,
                node.MaxConcurrency
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/nodes/{node.Id}", node);
        });

        var updateNode = group.MapPut("/nodes/{id:guid}", async (
            Guid id,
            UpdateNodeRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.FindAsync([id], cancellationToken);
            if (node is null) return Results.NotFound();
            var before = new { node.Name, node.BaseAddress, node.Weight, node.MaxConcurrency };
            node.Update(request.Name, request.BaseAddress, request.Weight, request.MaxConcurrency);
            AddAudit(dbContext, httpContext, "node.update", "node", node.Id.ToString(), new
            {
                before,
                after = new { node.Name, node.BaseAddress, node.Weight, node.MaxConcurrency }
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(node);
        });

        var testNodeConnection = group.MapPost("/nodes/{id:guid}/test-connection", async (
            Guid id,
            GatewayDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (node is null) return Results.NotFound();

            var serviceRoot = InferenceEndpoint.NormalizeBaseAddress(node.BaseAddress);
            var healthUrl = InferenceEndpoint.Combine(serviceRoot, "/health");
            var modelsUrl = InferenceEndpoint.Combine(serviceRoot, "/v1/models");
            var chatUrl = InferenceEndpoint.Combine(serviceRoot, "/v1/chat/completions");
            var responsesUrl = InferenceEndpoint.Combine(serviceRoot, "/v1/responses");
            var client = httpClientFactory.CreateClient("probe");

            var health = await ProbeAsync(client, healthUrl, cancellationToken);
            var openAi = await ProbeAsync(client, modelsUrl, cancellationToken);
            var success = health.Success && openAi.Success;

            AddAudit(dbContext, httpContext, "node.test_connection", "node", node.Id.ToString(), new
            {
                node.Name,
                serviceRoot,
                success,
                health = new { health.Success, health.StatusCode, health.LatencyMilliseconds, health.Error },
                openAi = new { openAi.Success, openAi.StatusCode, openAi.LatencyMilliseconds, openAi.Error }
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                nodeId = node.Id,
                nodeName = node.Name,
                serviceRoot,
                healthUrl = healthUrl.ToString(),
                modelsUrl = modelsUrl.ToString(),
                chatCompletionsUrl = chatUrl.ToString(),
                responsesUrl = responsesUrl.ToString(),
                success,
                health,
                openAi
            });
        });

        var drainNode = group.MapPost("/nodes/{id:guid}/drain", (Guid id) =>
            Results.Json(new
            {
                code = "legacy_drain_deprecated",
                message = "Use the coordinated maintenance drain endpoint. It establishes an admission block before persisting Draining state.",
                replacement = $"/api/admin/nodes/{id}/maintenance/drain"
            }, statusCode: StatusCodes.Status410Gone));

        var enableNode = group.MapPost("/nodes/{id:guid}/enable", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.FindAsync([id], cancellationToken);
            if (node is null) return Results.NotFound();
            node.Enable();
            AddAudit(dbContext, httpContext, "node.enable", "node", node.Id.ToString(), new { node.Name });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        var disableNode = group.MapPost("/nodes/{id:guid}/disable", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.FindAsync([id], cancellationToken);
            if (node is null) return Results.NotFound();
            node.Disable();
            AddAudit(dbContext, httpContext, "node.disable", "node", node.Id.ToString(), new { node.Name });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/models", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await dbContext.Models.AsNoTracking().OrderBy(model => model.PublicName).ToListAsync(cancellationToken)));

        var createModel = group.MapPost("/models", async (
            CreateModelRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var model = new ModelDefinition(request.PublicName, request.ProviderModelName, request.SupportsStreaming, request.SupportsTools);
            dbContext.Models.Add(model);
            AddAudit(dbContext, httpContext, "model.create", "model", model.Id.ToString(), new
            {
                model.PublicName,
                model.ProviderModelName,
                model.SupportsStreaming,
                model.SupportsTools
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/models/{model.Id}", model);
        });

        group.MapGet("/deployments", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await dbContext.Deployments.AsNoTracking().ToListAsync(cancellationToken)));

        var createDeployment = group.MapPost("/deployments", async (
            CreateDeploymentRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var nodeExists = await dbContext.Nodes.AnyAsync(node => node.Id == request.NodeId, cancellationToken);
            var modelExists = await dbContext.Models.AnyAsync(model => model.Id == request.ModelId, cancellationToken);
            if (!nodeExists || !modelExists)
            {
                return Results.BadRequest(new { error = "NodeId and ModelId must reference existing entities." });
            }

            var deployment = new ModelDeployment(request.NodeId, request.ModelId, request.Weight, request.MaxConcurrency);
            dbContext.Deployments.Add(deployment);
            AddAudit(dbContext, httpContext, "deployment.create", "deployment", deployment.Id.ToString(), new
            {
                deployment.NodeId,
                deployment.ModelId,
                deployment.Weight,
                deployment.MaxConcurrency
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/deployments/{deployment.Id}", deployment);
        });

        var updateDeployment = group.MapPut("/deployments/{id:guid}", async (
            Guid id,
            UpdateDeploymentRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var deployment = await dbContext.Deployments.FindAsync([id], cancellationToken);
            if (deployment is null) return Results.NotFound();
            var before = new { deployment.Weight, deployment.MaxConcurrency, deployment.Enabled };
            deployment.SetCapacity(request.Weight, request.MaxConcurrency);
            if (request.Enabled) deployment.Enable(); else deployment.Disable();
            AddAudit(dbContext, httpContext, "deployment.update", "deployment", deployment.Id.ToString(), new
            {
                before,
                after = new { deployment.Weight, deployment.MaxConcurrency, deployment.Enabled }
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(deployment);
        });

        group.MapGet("/api-credentials", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await dbContext.ApiCredentials.AsNoTracking().OrderByDescending(item => item.CreatedAtUtc).Select(item => new
            {
                item.Id,
                item.Name,
                item.KeyPrefix,
                item.Enabled,
                item.CreatedAtUtc,
                item.ExpiresAtUtc,
                item.LastUsedAtUtc
            }).ToListAsync(cancellationToken)));

        var createCredential = group.MapPost("/api-credentials", async (
            CreateApiCredentialRequest request,
            GatewayDbContext dbContext,
            ApiKeyHasher hasher,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var secret = ApiKeyHasher.GenerateSecret();
            var credential = new ApiCredential(request.Name, ApiKeyHasher.GetPrefix(secret), hasher.Hash(secret), request.ExpiresAtUtc);
            dbContext.ApiCredentials.Add(credential);
            AddAudit(dbContext, httpContext, "credential.create", "api_credential", credential.Id.ToString(), new
            {
                credential.Name,
                credential.KeyPrefix,
                credential.ExpiresAtUtc
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(new
            {
                credential.Id,
                credential.Name,
                credential.KeyPrefix,
                credential.CreatedAtUtc,
                credential.ExpiresAtUtc,
                secret
            });
        });

        var revokeCredential = group.MapPost("/api-credentials/{id:guid}/revoke", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var credential = await dbContext.ApiCredentials.FindAsync([id], cancellationToken);
            if (credential is null) return Results.NotFound();
            credential.Revoke();
            AddAudit(dbContext, httpContext, "credential.revoke", "api_credential", credential.Id.ToString(), new
            {
                credential.Name,
                credential.KeyPrefix
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/metrics", async (int? take, GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var size = Math.Clamp(take ?? 100, 1, 500);
            var rows = await dbContext.RequestMetrics.AsNoTracking()
                .OrderByDescending(metric => metric.StartedAtUtc)
                .Take(size)
                .ToListAsync(cancellationToken);
            return Results.Ok(rows);
        });

        group.MapGet("/audit", async (int? take, GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var size = Math.Clamp(take ?? 100, 1, 500);
            var rows = await dbContext.AuditEvents.AsNoTracking()
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(size)
                .ToListAsync(cancellationToken);
            return Results.Ok(rows);
        });

        if (entraEnabled)
        {
            foreach (var endpoint in new[]
            {
                updateRouting,
                createNode,
                updateNode,
                testNodeConnection,
                drainNode,
                enableNode,
                disableNode,
                createModel,
                createDeployment,
                updateDeployment,
                createCredential,
                revokeCredential
            })
            {
                endpoint.RequireAuthorization("AdminWrite");
            }
        }

        return endpoints;
    }

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        string entityType,
        string entityId,
        object? details = null)
    {
        var actor = ResolveActor(httpContext);
        var sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var detailsJson = details is null ? null : JsonSerializer.Serialize(details);
        dbContext.AuditEvents.Add(new AuditEvent(actor, action, entityType, entityId, sourceIp, detailsJson));
    }

    private static string ResolveActor(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return "local-admin";
        }

        return httpContext.User.FindFirst("preferred_username")?.Value
            ?? httpContext.User.FindFirst(ClaimTypes.Email)?.Value
            ?? httpContext.User.Identity?.Name
            ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "authenticated-admin";
    }

    private static async Task<EndpointProbe> ProbeAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new EndpointProbe(
                url.ToString(),
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EndpointProbe(url.ToString(), false, null, stopwatch.ElapsedMilliseconds, "Probe timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new EndpointProbe(url.ToString(), false, null, stopwatch.ElapsedMilliseconds, exception.Message);
        }
    }

    public sealed record CreateNodeRequest(string Name, string BaseAddress, int Weight = 1, int MaxConcurrency = 4);
    public sealed record UpdateNodeRequest(string Name, string BaseAddress, int Weight = 1, int MaxConcurrency = 4);
    public sealed record UpdateRoutingRequest(RoutingStrategy Strategy);
    public sealed record CreateModelRequest(string PublicName, string ProviderModelName, bool SupportsStreaming = true, bool SupportsTools = true);
    public sealed record CreateDeploymentRequest(Guid NodeId, Guid ModelId, int Weight = 1, int? MaxConcurrency = null);
    public sealed record UpdateDeploymentRequest(int Weight = 1, int? MaxConcurrency = null, bool Enabled = true);
    public sealed record CreateApiCredentialRequest(string Name, DateTimeOffset? ExpiresAtUtc = null);
    public sealed record EndpointProbe(string Url, bool Success, int? StatusCode, long LatencyMilliseconds, string? Error);
}