using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;

namespace LlmProxy.Api.Admin;

public static class NodeHardwareMetricsEndpoints
{
    public static IEndpointRouteBuilder MapNodeHardwareMetricsEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/nodes");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        var update = group.MapPut("/{id:guid}/hardware-metrics", async (
            Guid id,
            UpdateHardwareMetricsRequest request,
            GatewayDbContext dbContext,
            INodeHardwareMetricsTracker tracker,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.FindAsync([id], cancellationToken);
            if (node is null)
            {
                return Results.NotFound();
            }

            var previous = node.HardwareMetricsBaseAddress;
            try
            {
                node.SetHardwareMetricsBaseAddress(request.BaseAddress);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "baseAddress", message = exception.Message });
            }

            dbContext.AuditEvents.Add(new AuditEvent(
                ResolveActor(httpContext),
                "node.hardware_metrics.update",
                "node",
                node.Id.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new
                {
                    previous,
                    current = node.HardwareMetricsBaseAddress
                })));

            await dbContext.SaveChangesAsync(cancellationToken);
            if (node.HardwareMetricsBaseAddress is null)
            {
                tracker.Remove(node.Id);
            }

            return Results.Ok(new
            {
                node.Id,
                node.HardwareMetricsBaseAddress
            });
        });

        if (entraEnabled)
        {
            update.RequireAuthorization("AdminWrite");
        }

        return endpoints;
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

    public sealed record UpdateHardwareMetricsRequest(string? BaseAddress);
}
