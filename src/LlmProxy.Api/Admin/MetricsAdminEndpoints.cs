using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Telemetry;

namespace LlmProxy.Api.Admin;

public static class MetricsAdminEndpoints
{
    public static IEndpointRouteBuilder MapMetricsAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("/metrics/summary", async (
            int? hours,
            MetricsSummaryReader summaryReader,
            CancellationToken cancellationToken) =>
        {
            var windowHours = Math.Clamp(hours ?? 24, 1, 168);
            return Results.Ok(await summaryReader.ReadAsync(windowHours, cancellationToken));
        });

        group.MapGet("/routing/performance", (IDeploymentPerformanceTracker tracker) =>
            Results.Ok(tracker.GetSnapshots()));

        group.MapGet("/routing/runtime", (INodeRuntimeMetricsTracker tracker) =>
            Results.Ok(tracker.GetSnapshots()));

        group.MapGet("/hardware", (INodeHardwareMetricsTracker tracker) =>
            Results.Ok(tracker.GetSnapshots()));

        return endpoints;
    }
}
