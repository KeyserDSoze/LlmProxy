using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class NodeMaintenanceAdminEndpoints
{
    public static IEndpointRouteBuilder MapNodeMaintenanceAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/nodes/{id:guid}/maintenance");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("", async (
            Guid id,
            GatewayDbContext dbContext,
            INodeMaintenanceCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (node is null) return Results.NotFound();

            var coordination = await coordinator.GetStatusAsync(id, cancellationToken);
            return Results.Ok(ToStatus(node, coordination));
        });

        var beginDrain = group.MapPost("/drain", async (
            Guid id,
            GatewayDbContext dbContext,
            INodeMaintenanceCoordinator coordinator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (node is null) return Results.NotFound();
            if (!node.Enabled)
            {
                return Results.Conflict(new { code = "node_disabled", message = "A disabled node cannot enter maintenance drain." });
            }

            if (node.Status == NodeStatus.Draining)
            {
                var confirmed = await coordinator.TryConfirmDrainAsync(id, cancellationToken);
                var current = await coordinator.GetStatusAsync(id, cancellationToken);
                return Results.Ok(new
                {
                    status = ToStatus(node, current),
                    coordinationPending = !confirmed
                });
            }

            if (!await coordinator.TryBeginDrainAsync(id, cancellationToken))
            {
                return Results.Json(
                    new { code = "maintenance_coordination_unavailable", message = "Could not establish the admission block required for a safe drain." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            try
            {
                node.StartDrain();
                AddAudit(dbContext, httpContext, "node.maintenance.drain", "node", node.Id.ToString(), new { node.Name });
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await coordinator.CancelPendingDrainAsync(id, CancellationToken.None);
                throw;
            }

            var coordinationConfirmed = await coordinator.TryConfirmDrainAsync(id, cancellationToken);
            var coordination = await coordinator.GetStatusAsync(id, cancellationToken);
            return Results.Json(new
            {
                status = ToStatus(node, coordination),
                coordinationPending = !coordinationConfirmed
            }, statusCode: StatusCodes.Status202Accepted);
        });

        var resume = group.MapPost("/resume", async (
            Guid id,
            GatewayDbContext dbContext,
            INodeMaintenanceCoordinator coordinator,
            IHttpClientFactory httpClientFactory,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (node is null) return Results.NotFound();

            var coordination = await coordinator.GetStatusAsync(id, cancellationToken);
            if (!coordination.CoordinationAvailable)
            {
                return Results.Json(new
                {
                    code = "maintenance_coordination_unavailable",
                    message = "Drain completion cannot be proven while distributed maintenance coordination is unavailable.",
                    status = ToStatus(node, coordination)
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // A previous resume may have committed Healthy state but failed to clear the Redis marker.
            // Retrying the operation repairs only that coordination residue; no new health transition is needed.
            if (node.Status == NodeStatus.Healthy && coordination.AdmissionBlocked)
            {
                var repaired = await coordinator.TryResumeAsync(id, cancellationToken);
                var repairedStatus = await coordinator.GetStatusAsync(id, cancellationToken);
                return repaired
                    ? Results.Ok(new { status = ToStatus(node, repairedStatus), repairedCoordination = true })
                    : Results.Json(new
                    {
                        code = "maintenance_coordination_unavailable",
                        message = "The node is validated but the admission block could not yet be cleared.",
                        status = ToStatus(node, repairedStatus)
                    }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (node.Status != NodeStatus.Draining)
            {
                return Results.Conflict(new
                {
                    code = "node_not_draining",
                    message = $"Node '{node.Name}' is {node.Status} and has no draining maintenance session."
                });
            }

            // Legacy drain calls may predate the distributed maintenance marker. Promote them to the
            // safe coordination path before deciding that the node is empty.
            if (!coordination.AdmissionBlocked)
            {
                if (!await coordinator.TryConfirmDrainAsync(id, cancellationToken))
                {
                    return Results.Json(new
                    {
                        code = "maintenance_coordination_unavailable",
                        message = "Could not establish the maintenance admission block before resume validation."
                    }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                coordination = await coordinator.GetStatusAsync(id, cancellationToken);
            }

            if (!coordination.CoordinationAvailable || coordination.ActiveRequests != 0)
            {
                return Results.Conflict(new
                {
                    code = "node_still_draining",
                    message = "The node cannot resume until all admitted inference work has completed.",
                    status = ToStatus(node, coordination)
                });
            }

            var client = httpClientFactory.CreateClient("maintenance");
            var serviceRoot = InferenceEndpoint.NormalizeBaseAddress(node.BaseAddress);
            var health = await ProbeGetAsync(client, InferenceEndpoint.Combine(serviceRoot, "/health"), cancellationToken);
            var models = await ProbeGetAsync(client, InferenceEndpoint.Combine(serviceRoot, "/v1/models"), cancellationToken);

            var providerModels = await (
                from deployment in dbContext.Deployments.AsNoTracking()
                join model in dbContext.Models.AsNoTracking() on deployment.ModelId equals model.Id
                where deployment.NodeId == id && deployment.Enabled && model.Enabled
                select model.ProviderModelName)
                .Distinct()
                .OrderBy(name => name)
                .ToListAsync(cancellationToken);

            var warmups = new List<MaintenanceProbe>();
            if (health.Success && models.Success)
            {
                foreach (var providerModel in providerModels)
                {
                    warmups.Add(await WarmupAsync(client, serviceRoot, providerModel, cancellationToken));
                    if (!warmups[^1].Success)
                    {
                        break;
                    }
                }
            }

            var validationSucceeded = health.Success && models.Success && warmups.All(item => item.Success);
            if (!validationSucceeded)
            {
                AddAudit(dbContext, httpContext, "node.maintenance.resume_failed", "node", node.Id.ToString(), new
                {
                    node.Name,
                    health,
                    models,
                    warmups
                });
                await dbContext.SaveChangesAsync(cancellationToken);

                return Results.Json(new
                {
                    code = "node_validation_failed",
                    message = "The node remains Draining because health/model validation or inference warm-up failed.",
                    health,
                    models,
                    warmups
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Reuse existing domain transitions without exposing an unsafe Unknown window in durable/runtime state:
            // only the final Healthy state is saved and published after every validation has succeeded.
            node.Disable();
            node.Enable();
            node.RecordHealthSuccess(DateTimeOffset.UtcNow, health.LatencyMilliseconds, healthyAfterSuccesses: 1);
            AddAudit(dbContext, httpContext, "node.maintenance.resume", "node", node.Id.ToString(), new
            {
                node.Name,
                providerModels,
                healthLatencyMilliseconds = health.LatencyMilliseconds
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            var coordinationCleared = await coordinator.TryResumeAsync(id, cancellationToken);
            var finalCoordination = await coordinator.GetStatusAsync(id, cancellationToken);
            var payload = new
            {
                status = ToStatus(node, finalCoordination),
                health,
                models,
                warmups,
                coordinationPending = !coordinationCleared
            };

            return coordinationCleared
                ? Results.Ok(payload)
                : Results.Json(payload, statusCode: StatusCodes.Status202Accepted);
        });

        if (entraEnabled)
        {
            beginDrain.RequireAuthorization("AdminWrite");
            resume.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static object ToStatus(InferenceNode node, NodeMaintenanceStatus coordination) => new
    {
        nodeId = node.Id,
        nodeName = node.Name,
        nodeStatus = node.Status,
        node.Enabled,
        coordination.Provider,
        coordination.CoordinationAvailable,
        coordination.AdmissionBlocked,
        coordination.ActiveRequests,
        drained = node.Status == NodeStatus.Draining && coordination.Drained
    };

    private static async Task<MaintenanceProbe> ProbeGetAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new MaintenanceProbe(
                url.ToString(),
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MaintenanceProbe(url.ToString(), false, null, stopwatch.ElapsedMilliseconds, "Probe timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new MaintenanceProbe(url.ToString(), false, null, stopwatch.ElapsedMilliseconds, exception.Message);
        }
    }

    private static async Task<MaintenanceProbe> WarmupAsync(
        HttpClient client,
        string serviceRoot,
        string providerModelName,
        CancellationToken cancellationToken)
    {
        var url = InferenceEndpoint.Combine(serviceRoot, "/v1/chat/completions");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new
                {
                    model = providerModelName,
                    max_tokens = 1,
                    stream = false,
                    messages = new[] { new { role = "user", content = "ping" } }
                })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new MaintenanceProbe(
                url.ToString(),
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                response.IsSuccessStatusCode ? null : $"Warm-up for '{providerModelName}' returned HTTP {(int)response.StatusCode}." );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MaintenanceProbe(url.ToString(), false, null, stopwatch.ElapsedMilliseconds, $"Warm-up for '{providerModelName}' timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new MaintenanceProbe(url.ToString(), false, null, stopwatch.ElapsedMilliseconds, exception.Message);
        }
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

    public sealed record MaintenanceProbe(
        string Url,
        bool Success,
        int? StatusCode,
        long LatencyMilliseconds,
        string? Error);
}
