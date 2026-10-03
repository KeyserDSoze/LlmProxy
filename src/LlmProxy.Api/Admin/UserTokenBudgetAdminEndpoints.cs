using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
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
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.UserRateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            policy.ClearOutputTokenBudget();
            var actor = httpContext.User.Identity?.IsAuthenticated == true
                ? httpContext.User.FindFirstValue("preferred_username")
                  ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
                  ?? httpContext.User.Identity?.Name
                  ?? "authenticated-admin"
                : "local-admin";
            dbContext.AuditEvents.Add(new AuditEvent(
                actor,
                "user_output_token_budget.clear",
                "user_rate_limit_policy",
                policy.Id.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { policy.OwnerTenantId, policy.OwnerObjectId, policy.LogicalModel })));
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
