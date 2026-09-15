using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Retention;

namespace LlmProxy.Api.Admin;

public static class DataRetentionAdminEndpoints
{
    public static IEndpointRouteBuilder MapDataRetentionAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var get = endpoints.MapGet("/api/admin/retention", (DataRetentionService service) =>
            Results.Ok(new
            {
                service.Settings.Enabled,
                service.Settings.RequestMetricsDays,
                service.Settings.AuditEventsDays,
                service.Settings.RuntimeStateOutboxDays,
                service.Settings.IntervalHours,
                service.Settings.BatchSize
            }));

        var run = endpoints.MapPost("/api/admin/retention/run", async (
            DataRetentionService service,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RunAsync(cancellationToken: cancellationToken);
            var actor = ResolveActor(httpContext);
            var sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
            var details = JsonSerializer.Serialize(new
            {
                result.DeletedRequestMetrics,
                result.DeletedAuditEvents,
                result.DeletedRuntimeStateOutbox,
                result.RequestMetricsCutoffUtc,
                result.AuditEventsCutoffUtc,
                result.RuntimeStateOutboxCutoffUtc
            });

            dbContext.AuditEvents.Add(new AuditEvent(
                actor,
                "retention.cleanup.run",
                "data_retention",
                "manual",
                sourceIp,
                details));
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(result);
        });

        if (entraEnabled)
        {
            get.RequireAuthorization("AdminRead");
            run.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static string ResolveActor(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return "local-admin";
        }

        return httpContext.User.FindFirstValue("preferred_username")
            ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
            ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "entra-user";
    }
}
