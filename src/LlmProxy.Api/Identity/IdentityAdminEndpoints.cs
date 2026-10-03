using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Identity;

public static class IdentityAdminEndpoints
{
    public static IEndpointRouteBuilder MapIdentityAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/identity");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("/me", (HttpContext httpContext) =>
        {
            if (!entraEnabled)
            {
                return Results.Ok(new
                {
                    tenantId = (string?)null,
                    objectId = (string?)null,
                    principalName = (string?)null,
                    displayName = (string?)null,
                    roles = Array.Empty<string>(),
                    isAdmin = true
                });
            }

            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return Results.Json(new
                {
                    error = "entra_identity_missing",
                    message = "The authenticated token must contain stable Entra tenant (tid) and object (oid) claims."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new
            {
                identity.TenantId,
                identity.ObjectId,
                identity.PrincipalName,
                identity.DisplayName,
                identity.Roles,
                isAdmin = identity.Roles.Contains("LlmProxy.Admin", StringComparer.OrdinalIgnoreCase)
            });
        });

        group.MapGet("/api-credentials", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var rows = await dbContext.ApiCredentials.AsNoTracking()
                .OrderByDescending(item => item.CreatedAtUtc)
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.KeyPrefix,
                    item.Enabled,
                    item.CreatedAtUtc,
                    item.ExpiresAtUtc,
                    item.LastUsedAtUtc,
                    item.UsageGroupId,
                    kind = item.OwnerObjectId == null ? "service" : "personal",
                    item.OwnerTenantId,
                    item.OwnerObjectId,
                    item.OwnerPrincipalName
                })
                .ToListAsync(cancellationToken);
            return Results.Ok(rows);
        });

        group.MapGet("/users", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var rows = await dbContext.ApiCredentials.AsNoTracking()
                .Where(item => item.OwnerTenantId != null && item.OwnerObjectId != null)
                .GroupBy(item => new { item.OwnerTenantId, item.OwnerObjectId })
                .Select(grouping => new
                {
                    tenantId = grouping.Key.OwnerTenantId,
                    objectId = grouping.Key.OwnerObjectId,
                    principalName = grouping.Max(item => item.OwnerPrincipalName),
                    credentialCount = grouping.Count(),
                    activeCredentialCount = grouping.Count(item => item.Enabled),
                    lastUsedAtUtc = grouping.Max(item => item.LastUsedAtUtc),
                    firstCredentialCreatedAtUtc = grouping.Min(item => item.CreatedAtUtc)
                })
                .OrderBy(item => item.principalName)
                .ThenBy(item => item.objectId)
                .ToListAsync(cancellationToken);
            return Results.Ok(rows);
        });

        return endpoints;
    }
}
