using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class UserTokenBudgetAdminEndpoints
{
    public static IEndpointRouteBuilder MapUserTokenBudgetAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var endpoint = endpoints.MapDelete("/api/admin/user-rate-limits/{id:guid}/output-token-budget", async (
            Guid id,
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.UserRateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            policy.ClearOutputTokenBudget();
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }
}
