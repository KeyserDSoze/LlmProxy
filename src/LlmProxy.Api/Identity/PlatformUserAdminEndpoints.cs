using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Identity;

public static class PlatformUserAdminEndpoints
{
    public static IEndpointRouteBuilder MapPlatformUserAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/users");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminWrite");
        }

        group.MapGet("/settings", async (
            GatewayDbContext dbContext,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var settings = await dbContext.UserAccessSettings
                .AsNoTracking()
                .SingleAsync(item => item.Id == UserAccessSettingsRecord.SingletonId, cancellationToken);

            return Results.Ok(new
            {
                settings.ProvisioningMode,
                settings.UpdatedAtUtc,
                configuredTenantId = configuration["EntraId:TenantId"]
            });
        });

        group.MapPut("/settings", async (
            UpdateUserAccessSettingsRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var mode = NormalizeMode(request.ProvisioningMode);
            if (mode is null)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_provisioning_mode",
                    message = "ProvisioningMode must be 'automatic' or 'manual'."
                });
            }

            var settings = await dbContext.UserAccessSettings
                .SingleAsync(item => item.Id == UserAccessSettingsRecord.SingletonId, cancellationToken);
            var previousMode = settings.ProvisioningMode;
            settings.ProvisioningMode = mode;
            settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

            AddAudit(dbContext, httpContext, "user.provisioning_mode.update", "settings", new
            {
                previousMode,
                provisioningMode = mode
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                settings.ProvisioningMode,
                settings.UpdatedAtUtc
            });
        });

        group.MapGet("", async (
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var sinceUtc = DateTimeOffset.UtcNow.AddDays(-30);

            var credentialStats = await dbContext.ApiCredentials.AsNoTracking()
                .Where(item => item.OwnerTenantId != null && item.OwnerObjectId != null)
                .GroupBy(item => new { item.OwnerTenantId, item.OwnerObjectId })
                .Select(grouping => new
                {
                    grouping.Key.OwnerTenantId,
                    grouping.Key.OwnerObjectId,
                    CredentialCount = grouping.Count(),
                    ActiveCredentialCount = grouping.Count(item => item.Enabled),
                    LastCredentialUsedAtUtc = grouping.Max(item => item.LastUsedAtUtc)
                })
                .ToListAsync(cancellationToken);

            var requestStats = await (
                    from metric in dbContext.RequestMetrics.AsNoTracking()
                    join credential in dbContext.ApiCredentials.AsNoTracking()
                        on metric.ApiCredentialId equals (Guid?)credential.Id
                    where metric.StartedAtUtc >= sinceUtc &&
                          credential.OwnerTenantId != null &&
                          credential.OwnerObjectId != null
                    group metric by new { credential.OwnerTenantId, credential.OwnerObjectId }
                    into grouping
                    select new
                    {
                        grouping.Key.OwnerTenantId,
                        grouping.Key.OwnerObjectId,
                        RequestCount30d = grouping.LongCount(),
                        ErrorCount30d = grouping.LongCount(item => item.StatusCode >= 400)
                    })
                .ToListAsync(cancellationToken);

            var credentialLookup = credentialStats.ToDictionary(
                item => Key(item.OwnerTenantId!, item.OwnerObjectId!),
                StringComparer.OrdinalIgnoreCase);
            var requestLookup = requestStats.ToDictionary(
                item => Key(item.OwnerTenantId!, item.OwnerObjectId!),
                StringComparer.OrdinalIgnoreCase);

            var users = await dbContext.PlatformUsers.AsNoTracking()
                .OrderBy(item => item.PrincipalName)
                .ThenBy(item => item.ObjectId)
                .ToListAsync(cancellationToken);
            var usageGroupNames = await dbContext.UsageGroups.AsNoTracking()
                .ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);

            return Results.Ok(users.Select(user =>
            {
                credentialLookup.TryGetValue(Key(user.TenantId, user.ObjectId), out var credential);
                requestLookup.TryGetValue(Key(user.TenantId, user.ObjectId), out var requests);
                return new
                {
                    user.Id,
                    user.TenantId,
                    user.ObjectId,
                    user.PrincipalName,
                    user.DisplayName,
                    user.UsageGroupId,
                    usageGroupName = user.UsageGroupId is Guid usageGroupId && usageGroupNames.TryGetValue(usageGroupId, out var groupName) ? groupName : null,
                    user.Enabled,
                    user.ProvisioningSource,
                    user.CreatedAtUtc,
                    user.LastSeenAtUtc,
                    user.DisabledAtUtc,
                    credentialCount = credential?.CredentialCount ?? 0,
                    activeCredentialCount = credential?.ActiveCredentialCount ?? 0,
                    lastCredentialUsedAtUtc = credential?.LastCredentialUsedAtUtc,
                    requestCount30d = requests?.RequestCount30d ?? 0,
                    errorCount30d = requests?.ErrorCount30d ?? 0
                };
            }));
        });

        group.MapPost("", async (
            CreatePlatformUserRequest request,
            GatewayDbContext dbContext,
            IConfiguration configuration,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var tenantId = string.IsNullOrWhiteSpace(request.TenantId)
                ? configuration["EntraId:TenantId"]
                : request.TenantId;
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(request.ObjectId))
            {
                return Results.BadRequest(new
                {
                    error = "stable_identity_required",
                    message = "TenantId and ObjectId are required. TenantId may be omitted only when EntraId:TenantId is configured."
                });
            }

            tenantId = NormalizeStableId(tenantId);
            var objectId = NormalizeStableId(request.ObjectId);
            var tenantLookup = tenantId.ToUpperInvariant();
            var objectLookup = objectId.ToUpperInvariant();

            var existing = await dbContext.PlatformUsers.SingleOrDefaultAsync(
                item => item.TenantId.ToUpper() == tenantLookup && item.ObjectId.ToUpper() == objectLookup,
                cancellationToken);
            if (existing is not null)
            {
                return Results.Conflict(new
                {
                    error = "user_already_registered",
                    existing.Id
                });
            }

            if (request.UsageGroupId is Guid requestedGroupId &&
                !await dbContext.UsageGroups.AnyAsync(item => item.Id == requestedGroupId, cancellationToken))
            {
                return Results.BadRequest(new { error = "UsageGroupId must reference an existing usage group." });
            }

            var user = new PlatformUserRecord
            {
                TenantId = tenantId,
                ObjectId = objectId,
                PrincipalName = Clean(request.PrincipalName),
                DisplayName = Clean(request.DisplayName),
                UsageGroupId = request.UsageGroupId,
                Enabled = request.Enabled ?? true,
                ProvisioningSource = "admin",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                DisabledAtUtc = request.Enabled == false ? DateTimeOffset.UtcNow : null
            };
            dbContext.PlatformUsers.Add(user);
            AddAudit(dbContext, httpContext, "user.create", user.Id.ToString(), new
            {
                user.TenantId,
                user.ObjectId,
                user.PrincipalName,
                user.Enabled
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(user);
        });

        group.MapPost("/{id:guid}/disable", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var user = await dbContext.PlatformUsers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (!user.Enabled)
            {
                return Results.NoContent();
            }

            user.Enabled = false;
            user.DisabledAtUtc = DateTimeOffset.UtcNow;

            var tenantLookup = user.TenantId.ToUpperInvariant();
            var objectLookup = user.ObjectId.ToUpperInvariant();
            var credentials = await dbContext.ApiCredentials
                .Where(item => item.OwnerTenantId != null &&
                               item.OwnerObjectId != null &&
                               item.OwnerTenantId.ToUpper() == tenantLookup &&
                               item.OwnerObjectId.ToUpper() == objectLookup &&
                               item.Enabled)
                .ToListAsync(cancellationToken);
            foreach (var credential in credentials)
            {
                credential.Revoke();
            }

            AddAudit(dbContext, httpContext, "user.disable", user.Id.ToString(), new
            {
                user.TenantId,
                user.ObjectId,
                revokedCredentialCount = credentials.Count
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/enable", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var user = await dbContext.PlatformUsers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            user.Enabled = true;
            user.DisabledAtUtc = null;
            AddAudit(dbContext, httpContext, "user.enable", user.Id.ToString(), new
            {
                user.TenantId,
                user.ObjectId
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return endpoints;
    }

    private static string? NormalizeMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            UserAccessSettingsRecord.AutomaticMode => UserAccessSettingsRecord.AutomaticMode,
            UserAccessSettingsRecord.ManualMode => UserAccessSettingsRecord.ManualMode,
            _ => null
        };
    }

    private static string Key(string tenantId, string objectId) => $"{tenantId}|{objectId}";
    private static string NormalizeStableId(string value)
        => Guid.TryParse(value, out var parsed)
            ? parsed.ToString("D")
            : value.Trim().ToLowerInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        string entityId,
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
            "platform_user",
            entityId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            JsonSerializer.Serialize(details)));
    }

    public sealed record UpdateUserAccessSettingsRequest(string ProvisioningMode);
    public sealed record CreatePlatformUserRequest(
        string ObjectId,
        string? TenantId = null,
        string? PrincipalName = null,
        string? DisplayName = null,
        bool? Enabled = true,
        Guid? UsageGroupId = null);
}
