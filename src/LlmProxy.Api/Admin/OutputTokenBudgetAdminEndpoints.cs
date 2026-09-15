using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class OutputTokenBudgetAdminEndpoints
{
    public static IEndpointRouteBuilder MapOutputTokenBudgetAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("/output-token-budgets", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var policies = await dbContext.RateLimitPolicies.AsNoTracking()
                .Where(policy => policy.OutputTokensPerWindow != null)
                .OrderBy(policy => policy.ApiCredentialId)
                .ThenBy(policy => policy.LogicalModel)
                .Select(policy => new
                {
                    policy.Id,
                    policy.ApiCredentialId,
                    policy.LogicalModel,
                    policy.WindowSeconds,
                    policy.OutputTokensPerWindow,
                    policy.MaxOutputTokensPerRequest,
                    policy.Enabled,
                    policy.UpdatedAtUtc
                })
                .ToListAsync(cancellationToken);
            return Results.Ok(policies);
        });

        var upsert = group.MapPut("/rate-limits/{id:guid}/output-token-budget", async (
            Guid id,
            OutputTokenBudgetRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.RateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var before = new
            {
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest
            };

            try
            {
                policy.SetOutputTokenBudget(request.OutputTokensPerWindow, request.MaxOutputTokensPerRequest);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            AddAudit(dbContext, httpContext, "output_token_budget.update", "rate_limit_policy", policy.Id.ToString(), new
            {
                before,
                after = new { policy.OutputTokensPerWindow, policy.MaxOutputTokensPerRequest },
                policy.WindowSeconds
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                policy.Id,
                policy.ApiCredentialId,
                policy.LogicalModel,
                policy.WindowSeconds,
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest,
                policy.Enabled,
                policy.UpdatedAtUtc
            });
        });

        var clear = group.MapDelete("/rate-limits/{id:guid}/output-token-budget", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.RateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var before = new
            {
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest
            };
            policy.ClearOutputTokenBudget();
            AddAudit(dbContext, httpContext, "output_token_budget.clear", "rate_limit_policy", policy.Id.ToString(), new { before });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        if (entraEnabled)
        {
            upsert.RequireAuthorization("AdminWrite");
            clear.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        string entityType,
        string entityId,
        object? details)
    {
        var actor = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst("preferred_username")?.Value
              ?? httpContext.User.FindFirst(ClaimTypes.Email)?.Value
              ?? httpContext.User.Identity?.Name
              ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? "authenticated-admin"
            : "local-admin";
        var sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var detailsJson = details is null ? null : JsonSerializer.Serialize(details);
        dbContext.AuditEvents.Add(new AuditEvent(actor, action, entityType, entityId, sourceIp, detailsJson));
    }

    public sealed record OutputTokenBudgetRequest(
        int OutputTokensPerWindow,
        int MaxOutputTokensPerRequest);
}
