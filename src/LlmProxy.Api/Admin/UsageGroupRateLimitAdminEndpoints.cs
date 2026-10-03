using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Governance;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class UsageGroupRateLimitAdminEndpoints
{
    public static IEndpointRouteBuilder MapUsageGroupRateLimitAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/group-rate-limits");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var policies = await dbContext.UsageGroupRateLimitPolicies.AsNoTracking()
                .OrderBy(item => item.UsageGroupId)
                .ThenBy(item => item.LogicalModel)
                .ToListAsync(cancellationToken);
            var names = await dbContext.UsageGroups.AsNoTracking()
                .ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

            return Results.Ok(policies.Select(item => new
            {
                item.Id,
                item.UsageGroupId,
                usageGroupName = names.TryGetValue(item.UsageGroupId, out var name) ? name : null,
                item.LogicalModel,
                item.RequestsPerWindow,
                item.WindowSeconds,
                item.OutputTokensPerWindow,
                item.MaxOutputTokensPerRequest,
                item.Enabled,
                item.CreatedAtUtc,
                item.UpdatedAtUtc
            }));
        });

        var create = group.MapPost("", async (
            UpsertUsageGroupRateLimitRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!await dbContext.UsageGroups.AnyAsync(item => item.Id == request.UsageGroupId, cancellationToken))
            {
                return Results.BadRequest(new { error = "UsageGroupId must reference an existing usage group." });
            }

            var logicalModel = NormalizeModel(request.LogicalModel);
            if (logicalModel is not null &&
                !await dbContext.Models.AnyAsync(item => item.PublicName == logicalModel, cancellationToken))
            {
                return Results.BadRequest(new { error = "LogicalModel must reference an existing public model." });
            }

            if (await dbContext.UsageGroupRateLimitPolicies.AnyAsync(
                    item => item.UsageGroupId == request.UsageGroupId && item.LogicalModel == logicalModel,
                    cancellationToken))
            {
                return Results.Conflict(new { error = "A group quota already exists for this group/model scope." });
            }

            UsageGroupRateLimitPolicy policy;
            try
            {
                policy = new UsageGroupRateLimitPolicy(
                    request.UsageGroupId,
                    logicalModel,
                    request.RequestsPerWindow,
                    request.WindowSeconds,
                    request.Enabled,
                    request.OutputTokensPerWindow,
                    request.MaxOutputTokensPerRequest);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            dbContext.UsageGroupRateLimitPolicies.Add(policy);
            AddAudit(dbContext, httpContext, "group_rate_limit.create", policy, new
            {
                policy.UsageGroupId,
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds,
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest,
                policy.Enabled
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/group-rate-limits/{policy.Id}", policy);
        });

        var update = group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateUsageGroupRateLimitRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.UsageGroupRateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var logicalModel = NormalizeModel(request.LogicalModel);
            if (logicalModel is not null &&
                !await dbContext.Models.AnyAsync(item => item.PublicName == logicalModel, cancellationToken))
            {
                return Results.BadRequest(new { error = "LogicalModel must reference an existing public model." });
            }

            if (await dbContext.UsageGroupRateLimitPolicies.AnyAsync(
                    item => item.Id != id &&
                            item.UsageGroupId == policy.UsageGroupId &&
                            item.LogicalModel == logicalModel,
                    cancellationToken))
            {
                return Results.Conflict(new { error = "A group quota already exists for this group/model scope." });
            }

            try
            {
                policy.Update(logicalModel, request.RequestsPerWindow, request.WindowSeconds, request.Enabled);
                if (request.OutputTokensPerWindow.HasValue || request.MaxOutputTokensPerRequest.HasValue)
                {
                    if (!request.OutputTokensPerWindow.HasValue || !request.MaxOutputTokensPerRequest.HasValue)
                    {
                        return Results.BadRequest(new { error = "Output token budget fields must be supplied together." });
                    }

                    policy.SetOutputTokenBudget(
                        request.OutputTokensPerWindow.Value,
                        request.MaxOutputTokensPerRequest.Value);
                }
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            AddAudit(dbContext, httpContext, "group_rate_limit.update", policy, new
            {
                policy.UsageGroupId,
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds,
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest,
                policy.Enabled
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(policy);
        });

        var clearBudget = group.MapDelete("/{id:guid}/output-token-budget", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.UsageGroupRateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            policy.ClearOutputTokenBudget();
            AddAudit(dbContext, httpContext, "group_output_token_budget.clear", policy, new
            {
                policy.UsageGroupId,
                policy.LogicalModel
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        var delete = group.MapDelete("/{id:guid}", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.UsageGroupRateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            AddAudit(dbContext, httpContext, "group_rate_limit.delete", policy, new
            {
                policy.UsageGroupId,
                policy.LogicalModel
            });
            dbContext.UsageGroupRateLimitPolicies.Remove(policy);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        if (entraEnabled)
        {
            create.RequireAuthorization("AdminWrite");
            update.RequireAuthorization("AdminWrite");
            clearBudget.RequireAuthorization("AdminWrite");
            delete.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static string? NormalizeModel(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        UsageGroupRateLimitPolicy policy,
        object details)
    {
        var actor = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirstValue("preferred_username")
              ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
              ?? httpContext.User.Identity?.Name
              ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? "authenticated-admin"
            : "local-admin";

        dbContext.AuditEvents.Add(new AuditEvent(
            actor,
            action,
            "usage_group_rate_limit_policy",
            policy.Id.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            JsonSerializer.Serialize(details)));
    }

    public sealed record UpsertUsageGroupRateLimitRequest(
        Guid UsageGroupId,
        string? LogicalModel,
        int RequestsPerWindow,
        int WindowSeconds = 60,
        bool Enabled = true,
        int? OutputTokensPerWindow = null,
        int? MaxOutputTokensPerRequest = null);

    public sealed record UpdateUsageGroupRateLimitRequest(
        string? LogicalModel,
        int RequestsPerWindow,
        int WindowSeconds = 60,
        bool Enabled = true,
        int? OutputTokensPerWindow = null,
        int? MaxOutputTokensPerRequest = null);
}
