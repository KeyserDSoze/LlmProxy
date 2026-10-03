using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Identity;

public static class PlatformUserGroupEndpoints
{
    public static IEndpointRouteBuilder MapPlatformUserGroupEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var endpoint = endpoints.MapPut("/api/admin/users/{id:guid}/usage-group", async (
            Guid id,
            AssignPlatformUserUsageGroupRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var user = await dbContext.PlatformUsers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (request.UsageGroupId is Guid usageGroupId &&
                !await dbContext.UsageGroups.AnyAsync(item => item.Id == usageGroupId, cancellationToken))
            {
                return Results.BadRequest(new { error = "UsageGroupId must reference an existing usage group." });
            }

            var previousUsageGroupId = user.UsageGroupId;
            user.UsageGroupId = request.UsageGroupId;

            var tenantLookup = user.TenantId.ToUpperInvariant();
            var objectLookup = user.ObjectId.ToUpperInvariant();
            var credentials = await dbContext.ApiCredentials
                .Where(item => item.OwnerTenantId != null &&
                               item.OwnerObjectId != null &&
                               item.OwnerTenantId.ToUpper() == tenantLookup &&
                               item.OwnerObjectId.ToUpper() == objectLookup)
                .ToListAsync(cancellationToken);

            foreach (var credential in credentials)
            {
                if (request.UsageGroupId is Guid groupId)
                {
                    credential.AssignUsageGroup(groupId);
                }
                else
                {
                    credential.ClearUsageGroup();
                }
            }

            var actor = httpContext.User.Identity?.IsAuthenticated == true
                ? httpContext.User.FindFirstValue("preferred_username")
                  ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
                  ?? httpContext.User.Identity?.Name
                  ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? "authenticated-admin"
                : "local-admin";

            dbContext.AuditEvents.Add(new AuditEvent(
                actor,
                "user.usage_group.update",
                "platform_user",
                user.Id.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new
                {
                    user.TenantId,
                    user.ObjectId,
                    previousUsageGroupId,
                    usageGroupId = request.UsageGroupId,
                    affectedCredentialCount = credentials.Count
                })));

            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    public sealed record AssignPlatformUserUsageGroupRequest(Guid? UsageGroupId);
}
