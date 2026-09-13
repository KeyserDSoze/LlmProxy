using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class CapacityAdminEndpoints
{
    public static IEndpointRouteBuilder MapCapacityAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("/capacity", async (
            GatewayDbContext dbContext,
            IRequestLoadTracker loadTracker,
            CancellationToken cancellationToken) =>
        {
            var nodes = await dbContext.Nodes.AsNoTracking().OrderBy(node => node.Name).ToListAsync(cancellationToken);
            var deployments = await dbContext.Deployments.AsNoTracking().ToListAsync(cancellationToken);

            return Results.Ok(new
            {
                nodes = nodes.Select(node => new
                {
                    node.Id,
                    node.Name,
                    node.MaxConcurrency,
                    activeRequests = loadTracker.GetNodeActive(node.Id),
                    remaining = Math.Max(0, node.MaxConcurrency - loadTracker.GetNodeActive(node.Id))
                }),
                deployments = deployments.Select(deployment =>
                {
                    var node = nodes.Single(item => item.Id == deployment.NodeId);
                    return new
                    {
                        deployment.Id,
                        deployment.NodeId,
                        deployment.ModelId,
                        deployment.Enabled,
                        deployment.MaxConcurrency,
                        effectiveMaxConcurrency = deployment.MaxConcurrency ?? node.MaxConcurrency,
                        activeRequests = loadTracker.GetActive(deployment.Id),
                        deployment.RecommendedMaxConcurrency,
                        deployment.BenchmarkP95TtftMilliseconds,
                        deployment.BenchmarkP95DurationMilliseconds,
                        deployment.SustainableOutputTokensPerSecond,
                        deployment.BenchmarkSource,
                        deployment.BenchmarkMeasuredAtUtc
                    };
                })
            });
        });

        var updateProfile = group.MapPut("/deployments/{id:guid}/capacity-profile", async (
            Guid id,
            UpdateCapacityProfileRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var deployment = await dbContext.Deployments.FindAsync([id], cancellationToken);
            if (deployment is null)
            {
                return Results.NotFound();
            }

            try
            {
                deployment.SetCapacityProfile(
                    request.RecommendedMaxConcurrency,
                    request.P95TtftMilliseconds,
                    request.P95DurationMilliseconds,
                    request.SustainableOutputTokensPerSecond,
                    request.BenchmarkSource,
                    request.MeasuredAtUtc);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            AddAudit(dbContext, httpContext, "deployment.capacity_profile.update", "deployment", deployment.Id.ToString(), new
            {
                deployment.RecommendedMaxConcurrency,
                deployment.BenchmarkP95TtftMilliseconds,
                deployment.BenchmarkP95DurationMilliseconds,
                deployment.SustainableOutputTokensPerSecond,
                deployment.BenchmarkSource,
                deployment.BenchmarkMeasuredAtUtc
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(deployment);
        });

        var clearProfile = group.MapDelete("/deployments/{id:guid}/capacity-profile", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var deployment = await dbContext.Deployments.FindAsync([id], cancellationToken);
            if (deployment is null)
            {
                return Results.NotFound();
            }

            deployment.ClearCapacityProfile();
            AddAudit(dbContext, httpContext, "deployment.capacity_profile.clear", "deployment", deployment.Id.ToString());
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        var applyProfile = group.MapPost("/deployments/{id:guid}/capacity-profile/apply", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var deployment = await dbContext.Deployments.FindAsync([id], cancellationToken);
            if (deployment is null)
            {
                return Results.NotFound();
            }

            if (deployment.RecommendedMaxConcurrency is not int recommended)
            {
                return Results.BadRequest(new { error = "No capacity recommendation is available for this deployment." });
            }

            var node = await dbContext.Nodes.AsNoTracking().SingleAsync(item => item.Id == deployment.NodeId, cancellationToken);
            if (recommended > node.MaxConcurrency)
            {
                return Results.BadRequest(new
                {
                    error = $"Recommended concurrency {recommended} exceeds node '{node.Name}' physical limit {node.MaxConcurrency}. Increase the node limit explicitly first."
                });
            }

            var previous = deployment.MaxConcurrency;
            deployment.ApplyRecommendedCapacity();
            AddAudit(dbContext, httpContext, "deployment.capacity_profile.apply", "deployment", deployment.Id.ToString(), new
            {
                previousMaxConcurrency = previous,
                appliedMaxConcurrency = deployment.MaxConcurrency,
                nodeMaxConcurrency = node.MaxConcurrency,
                benchmarkSource = deployment.BenchmarkSource,
                benchmarkMeasuredAtUtc = deployment.BenchmarkMeasuredAtUtc
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(deployment);
        });

        if (entraEnabled)
        {
            updateProfile.RequireAuthorization("AdminWrite");
            clearProfile.RequireAuthorization("AdminWrite");
            applyProfile.RequireAuthorization("AdminWrite");
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

    public sealed record UpdateCapacityProfileRequest(
        int RecommendedMaxConcurrency,
        double? P95TtftMilliseconds,
        double? P95DurationMilliseconds,
        double? SustainableOutputTokensPerSecond,
        string BenchmarkSource,
        DateTimeOffset MeasuredAtUtc);
}
