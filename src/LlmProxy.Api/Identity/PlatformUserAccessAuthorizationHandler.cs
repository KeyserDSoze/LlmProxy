using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Identity;

public sealed class PlatformUserAccessAuthorizationHandler(
    GatewayDbContext dbContext,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<PlatformUserAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformUserAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (context.User.IsInRole(ConfiguredSuperAdminClaimsTransformation.AdminRole))
        {
            context.Succeed(requirement);
            return;
        }

        if (!EntraUserIdentityResolver.TryResolve(context.User, out var identity))
        {
            return;
        }

        var cancellationToken = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var tenantId = NormalizeStableId(identity.TenantId);
        var objectId = NormalizeStableId(identity.ObjectId);
        var tenantLookup = tenantId.ToUpperInvariant();
        var objectLookup = objectId.ToUpperInvariant();
        var user = await dbContext.PlatformUsers.SingleOrDefaultAsync(
            item => item.TenantId.ToUpper() == tenantLookup && item.ObjectId.ToUpper() == objectLookup,
            cancellationToken);

        if (user is null)
        {
            var settings = await dbContext.UserAccessSettings
                .AsNoTracking()
                .SingleAsync(item => item.Id == UserAccessSettingsRecord.SingletonId, cancellationToken);

            if (!string.Equals(settings.ProvisioningMode, UserAccessSettingsRecord.AutomaticMode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            user = new PlatformUserRecord
            {
                TenantId = tenantId,
                ObjectId = objectId,
                PrincipalName = identity.PrincipalName,
                DisplayName = identity.DisplayName,
                Enabled = true,
                ProvisioningSource = "automatic",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastSeenAtUtc = DateTimeOffset.UtcNow
            };
            dbContext.PlatformUsers.Add(user);
            dbContext.AuditEvents.Add(new AuditEvent(
                identity.AuditActor,
                "user.auto_provision",
                "platform_user",
                user.Id.ToString(),
                httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new
                {
                    user.TenantId,
                    user.ObjectId,
                    user.PrincipalName
                })));

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                dbContext.ChangeTracker.Clear();
                user = await dbContext.PlatformUsers.SingleOrDefaultAsync(
                    item => item.TenantId.ToUpper() == tenantLookup && item.ObjectId.ToUpper() == objectLookup,
                    cancellationToken);
                if (user is null)
                {
                    return;
                }
            }
        }

        if (!user.Enabled)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var profileChanged =
            !string.Equals(user.PrincipalName, identity.PrincipalName, StringComparison.Ordinal) ||
            !string.Equals(user.DisplayName, identity.DisplayName, StringComparison.Ordinal);
        var shouldTouch = user.LastSeenAtUtc is null || nowUtc - user.LastSeenAtUtc >= TimeSpan.FromMinutes(15);

        if (profileChanged || shouldTouch)
        {
            user.PrincipalName = identity.PrincipalName;
            user.DisplayName = identity.DisplayName;
            user.LastSeenAtUtc = nowUtc;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        context.Succeed(requirement);
    }

    private static string NormalizeStableId(string value)
        => Guid.TryParse(value, out var parsed)
            ? parsed.ToString("D")
            : value.Trim().ToLowerInvariant();
}
