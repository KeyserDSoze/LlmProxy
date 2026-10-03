using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Governance;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class UserRateLimitAdminEndpoints
{
    public static IEndpointRouteBuilder MapUserRateLimitAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/user-rate-limits");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var rows = await dbContext.UserRateLimitPolicies.AsNoTracking()
                .OrderBy(item => item.OwnerTenantId)
                .ThenBy(item => item.OwnerObjectId)
                .ThenBy(item => item.LogicalModel)
                .ToListAsync(cancellationToken);

            var principals = await dbContext.PlatformUsers.AsNoTracking()
                .Select(item => new
                {
                    item.TenantId,
                    item.ObjectId,
                    item.PrincipalName
                })
                .ToListAsync(cancellationToken);

            var principalNames = principals.ToDictionary(
                item => (item.TenantId.ToUpperInvariant(), item.ObjectId.ToUpperInvariant()),
                item => item.PrincipalName);

            return Results.Ok(rows.Select(item =>
            {
                principalNames.TryGetValue(
                    (item.OwnerTenantId.ToUpperInvariant(), item.OwnerObjectId.ToUpperInvariant()),
                    out var principalName);
                return new
                {
                    item.Id,
                    item.OwnerTenantId,
                    item.OwnerObjectId,
                    principalName,
                    item.LogicalModel,
                    item.RequestsPerWindow,
                    item.WindowSeconds,
                    item.OutputTokensPerWindow,
                    item.MaxOutputTokensPerRequest,
                    item.Enabled,
                    item.CreatedAtUtc,
                    item.UpdatedAtUtc
                };
            }));
        });

        var create = group.MapPost("", async (
            CreateUserRateLimitRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeIdentity(request.OwnerTenantId, out var tenantId) ||
                !TryNormalizeIdentity(request.OwnerObjectId, out var objectId))
            {
                return Results.BadRequest(new { error = "OwnerTenantId and OwnerObjectId are required and cannot exceed 64 characters." });
            }

            var logicalModel = NormalizeLogicalModel(request.LogicalModel);

            var ownerExists = await dbContext.PlatformUsers.AnyAsync(
                item => item.TenantId.ToUpper() == tenantId &&
                        item.ObjectId.ToUpper() == objectId,
                cancellationToken);
            if (!ownerExists)
            {
                return Results.BadRequest(new { error = "The Entra identity must be registered as a LlmProxy platform user before a user quota can be created." });
            }

            if (logicalModel is not null &&
                !await dbContext.Models.AnyAsync(model => model.PublicName == logicalModel, cancellationToken))
            {
                return Results.BadRequest(new { error = "LogicalModel must reference an existing public model." });
            }

            if (await HasDuplicateAsync(dbContext, null, tenantId, objectId, logicalModel, cancellationToken))
            {
                return Results.Conflict(new { error = "A user rate-limit policy already exists for this user/model scope." });
            }

            var policy = new UserRateLimitPolicy(
                tenantId,
                objectId,
                logicalModel,
                request.RequestsPerWindow,
                request.WindowSeconds,
                request.Enabled,
                request.OutputTokensPerWindow,
                request.MaxOutputTokensPerRequest);
            dbContext.UserRateLimitPolicies.Add(policy);
            AddAudit(dbContext, httpContext, "user_rate_limit.create", policy, new
            {
                policy.OwnerTenantId,
                policy.OwnerObjectId,
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds,
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest,
                policy.Enabled
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/user-rate-limits/{policy.Id}", policy);
        });

        var update = group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateUserRateLimitRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.UserRateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var logicalModel = NormalizeLogicalModel(request.LogicalModel);
            if (logicalModel is not null &&
                !await dbContext.Models.AnyAsync(model => model.PublicName == logicalModel, cancellationToken))
            {
                return Results.BadRequest(new { error = "LogicalModel must reference an existing public model." });
            }

            if (await HasDuplicateAsync(
                    dbContext,
                    id,
                    policy.OwnerTenantId.ToUpperInvariant(),
                    policy.OwnerObjectId.ToUpperInvariant(),
                    logicalModel,
                    cancellationToken))
            {
                return Results.Conflict(new { error = "A user rate-limit policy already exists for this user/model scope." });
            }

            var before = new
            {
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds,
                policy.Enabled
            };
            policy.Update(logicalModel, request.RequestsPerWindow, request.WindowSeconds, request.Enabled);
            AddAudit(dbContext, httpContext, "user_rate_limit.update", policy, new
            {
                before,
                after = new
                {
                    policy.LogicalModel,
                    policy.RequestsPerWindow,
                    policy.WindowSeconds,
                    policy.Enabled
                }
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(policy);
        });

        var delete = group.MapDelete("/{id:guid}", async (
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

            AddAudit(dbContext, httpContext, "user_rate_limit.delete", policy, new
            {
                policy.OwnerTenantId,
                policy.OwnerObjectId,
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds
            });
            dbContext.UserRateLimitPolicies.Remove(policy);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        if (entraEnabled)
        {
            create.RequireAuthorization("AdminWrite");
            update.RequireAuthorization("AdminWrite");
            delete.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static Task<bool> HasDuplicateAsync(
        GatewayDbContext dbContext,
        Guid? excludedId,
        string tenantId,
        string objectId,
        string? logicalModel,
        CancellationToken cancellationToken)
        => dbContext.UserRateLimitPolicies.AnyAsync(
            item => (!excludedId.HasValue || item.Id != excludedId.Value) &&
                    item.OwnerTenantId.ToUpper() == tenantId &&
                    item.OwnerObjectId.ToUpper() == objectId &&
                    item.LogicalModel == logicalModel,
            cancellationToken);

    private static bool TryNormalizeIdentity(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > 64)
        {
            return false;
        }

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    private static string? NormalizeLogicalModel(string? logicalModel) =>
        string.IsNullOrWhiteSpace(logicalModel) ? null : logicalModel.Trim();

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        UserRateLimitPolicy policy,
        object details)
    {
        dbContext.AuditEvents.Add(new AuditEvent(
            ResolveActor(httpContext),
            action,
            "user_rate_limit_policy",
            policy.Id.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            JsonSerializer.Serialize(details)));
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

    public sealed record CreateUserRateLimitRequest(
        string OwnerTenantId,
        string OwnerObjectId,
        string? LogicalModel,
        int RequestsPerWindow,
        int WindowSeconds = 60,
        bool Enabled = true,
        int? OutputTokensPerWindow = null,
        int? MaxOutputTokensPerRequest = null);

    public sealed record UpdateUserRateLimitRequest(
        string? LogicalModel,
        int RequestsPerWindow,
        int WindowSeconds = 60,
        bool Enabled = true,
        int? OutputTokensPerWindow = null,
        int? MaxOutputTokensPerRequest = null);
}
