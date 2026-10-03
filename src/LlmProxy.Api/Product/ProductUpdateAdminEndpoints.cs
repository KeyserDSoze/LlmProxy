using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;

namespace LlmProxy.Api.Product;

public static class ProductUpdateAdminEndpoints
{
    public static IEndpointRouteBuilder MapProductUpdateAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/updates");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("", async (
            ReleaseDiscoveryService releases,
            UpdateAgentClient agent,
            CancellationToken cancellationToken) =>
        {
            var product = ProductReleaseCatalog.GetInfo();
            var available = await releases.GetAvailableAsync(product.Version, cancellationToken);
            var status = await agent.TryGetStatusAsync(cancellationToken);
            return Results.Ok(new
            {
                currentVersion = product.Version,
                agentAvailable = status is not null,
                agent = status,
                releases = available
            });
        });

        var schedule = group.MapPost("", async (
            ScheduleProductUpdateRequest request,
            ReleaseDiscoveryService releases,
            UpdateAgentClient agent,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var current = ProductReleaseCatalog.GetInfo().Version;
            var available = await releases.GetAvailableAsync(current, cancellationToken);
            var target = available.FirstOrDefault(item =>
                item.IsNewer && string.Equals(item.Version, request.Version.Trim().TrimStart('v'), StringComparison.Ordinal));
            if (target is null)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_target_release",
                    message = "Target version must be a published stable release newer than the installed version."
                });
            }

            var upgradePath = available
                .Where(item => item.IsNewer && Version.TryParse(item.Version, out var parsed) && Version.TryParse(target.Version, out var targetVersion) && parsed <= targetVersion)
                .OrderBy(item => Version.Parse(item.Version))
                .Select(item => item.Version)
                .ToArray();

            try
            {
                var job = await agent.ScheduleAsync(request with { Version = target.Version }, upgradePath, cancellationToken);
                AddAudit(dbContext, httpContext, "product.update.schedule", job.Id.ToString(), new
                {
                    target.Version,
                    target.UpdateMode,
                    upgradePath,
                    job.ScheduledForUtc,
                    request.Force
                });
                await dbContext.SaveChangesAsync(cancellationToken);
                return Results.Accepted($"/api/admin/updates/{job.Id}", job);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        var cancel = group.MapDelete("/{id:guid}", async (
            Guid id,
            UpdateAgentClient agent,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!await agent.CancelAsync(id, cancellationToken))
                {
                    return Results.NotFound();
                }

                AddAudit(dbContext, httpContext, "product.update.cancel", id.ToString(), null);
                await dbContext.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        if (entraEnabled)
        {
            schedule.RequireAuthorization("AdminWrite");
            cancel.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        string entityId,
        object? details)
    {
        var actor = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst("preferred_username")?.Value
              ?? httpContext.User.Identity?.Name
              ?? "authenticated-admin"
            : "local-admin";
        dbContext.AuditEvents.Add(new AuditEvent(
            actor,
            action,
            "product_update",
            entityId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            details is null ? null : JsonSerializer.Serialize(details)));
    }
}
