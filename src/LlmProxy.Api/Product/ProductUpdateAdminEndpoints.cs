using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var product = ProductReleaseCatalog.GetInfo();
            var available = await releases.GetAvailableAsync(product.Version, cancellationToken);
            var status = await agent.TryGetStatusAsync(cancellationToken);
            var policy = await dbContext.ProductUpdatePolicies.AsNoTracking()
                .SingleAsync(item => item.Id == ProductUpdatePolicyRecord.SingletonId, cancellationToken);
            return Results.Ok(new
            {
                currentVersion = product.Version,
                agentAvailable = status is not null,
                agent = status,
                policy = ProductUpdatePolicySnapshot.From(policy),
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

        var updatePolicy = group.MapPut("/policy", async (
            UpdateProductPolicyRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var mode = request.Mode.Trim().ToLowerInvariant();
            if (!ProductUpdatePolicyRecord.IsSupportedMode(mode))
            {
                return Results.BadRequest(new { error = "invalid_update_mode", message = "Mode must be manual, asap, nightly, weekly or monthly." });
            }

            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId.Trim());
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return Results.BadRequest(new { error = "invalid_time_zone", message = "TimeZoneId must be a valid IANA/system time-zone identifier." });
            }

            if (request.LocalHour is < 0 or > 23 || request.LocalMinute is < 0 or > 59 ||
                request.DayOfWeek is < 0 or > 6 || request.DayOfMonth is < 1 or > 31)
            {
                return Results.BadRequest(new { error = "invalid_update_schedule", message = "The configured update schedule contains an invalid hour, minute, weekday or month day." });
            }

            var policy = await dbContext.ProductUpdatePolicies
                .SingleAsync(item => item.Id == ProductUpdatePolicyRecord.SingletonId, cancellationToken);
            var before = ProductUpdatePolicySnapshot.From(policy);
            policy.Mode = mode;
            policy.TimeZoneId = request.TimeZoneId.Trim();
            policy.LocalHour = request.LocalHour;
            policy.LocalMinute = request.LocalMinute;
            policy.DayOfWeek = request.DayOfWeek;
            policy.DayOfMonth = request.DayOfMonth;
            policy.LastCheckedAtUtc = mode == ProductUpdatePolicyRecord.AsapMode ? null : DateTimeOffset.UtcNow;
            policy.LastError = null;
            policy.UpdatedAtUtc = DateTimeOffset.UtcNow;

            AddAudit(dbContext, httpContext, "product.update.policy", ProductUpdatePolicyRecord.SingletonId.ToString(), new
            {
                before.Mode,
                after = new { policy.Mode, policy.TimeZoneId, policy.LocalHour, policy.LocalMinute, policy.DayOfWeek, policy.DayOfMonth }
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(ProductUpdatePolicySnapshot.From(policy));
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
            updatePolicy.RequireAuthorization("AdminWrite");
            cancel.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    public sealed record UpdateProductPolicyRequest(
        string Mode,
        string TimeZoneId,
        int LocalHour = 2,
        int LocalMinute = 0,
        int DayOfWeek = 0,
        int DayOfMonth = 1);

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
