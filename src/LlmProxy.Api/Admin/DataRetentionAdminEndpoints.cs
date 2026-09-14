using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
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
                service.Settings.IntervalHours,
                service.Settings.BatchSize
            }));

        var run = endpoints.MapPost("/api/admin/retention/run", async (
            DataRetentionService service,
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
                result.RequestMetricsCutoffUtc,
                result.AuditEventsCutoffUtc
            });

            serviceDbContext(service).AuditEvents.Add(new AuditEvent(
                actor,
                "retention.cleanup.run",
                "data_retention",
                "manual",
                sourceIp,
                details));
            await serviceDbContext(service).SaveChangesAsync(cancellationToken);

            return Results.Ok(result);
        });

        if (entraEnabled)
        {
            get.RequireAuthorization("AdminRead");
            run.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static LlmProxy.Infrastructure.Persistence.GatewayDbContext serviceDbContext(DataRetentionService service)
    {
        var field = typeof(DataRetentionService).GetField("<dbContext>P", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(service) as LlmProxy.Infrastructure.Persistence.GatewayDbContext
            ?? throw new InvalidOperationException("DataRetentionService DbContext is unavailable.");
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
