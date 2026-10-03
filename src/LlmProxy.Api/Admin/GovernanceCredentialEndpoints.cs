using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class GovernanceCredentialEndpoints
{
    public static IEndpointRouteBuilder MapGovernanceCredentialEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var endpoint = endpoints.MapGet("/api/admin/governance/credentials", async (
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var credentials = await dbContext.ApiCredentials.AsNoTracking()
                .OrderBy(item => item.Name)
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.KeyPrefix,
                    item.Enabled,
                    item.UsageGroupId,
                    item.EnforceCallerGovernance,
                    kind = item.IsPersonal ? "personal" : "organization",
                    item.OwnerTenantId,
                    item.OwnerObjectId,
                    item.OwnerPrincipalName,
                    item.CreatedAtUtc,
                    item.ExpiresAtUtc,
                    item.LastUsedAtUtc
                })
                .ToListAsync(cancellationToken);
            return Results.Ok(credentials);
        });

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminRead");
        }

        return endpoints;
    }
}
