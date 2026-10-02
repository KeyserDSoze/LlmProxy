using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using LlmProxy.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Identity;

public static class IdentitySelfServiceEndpoints
{
    public static IEndpointRouteBuilder MapIdentitySelfServiceEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        if (!entraEnabled)
        {
            return endpoints;
        }

        var group = endpoints.MapGroup("/api/me").RequireAuthorization("SelfService");

        group.MapGet("", (HttpContext httpContext) =>
        {
            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return InvalidIdentity();
            }

            return Results.Ok(new
            {
                identity.TenantId,
                identity.ObjectId,
                identity.PrincipalName,
                identity.DisplayName,
                identity.Roles
            });
        });

        group.MapGet("/api-credentials", async (
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return InvalidIdentity();
            }

            var credentials = await dbContext.ApiCredentials.AsNoTracking()
                .Where(item => item.OwnerTenantId == identity.TenantId && item.OwnerObjectId == identity.ObjectId)
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
                    item.UsageGroupId
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(credentials);
        });

        group.MapPost("/api-credentials", async (
            CreatePersonalApiCredentialRequest request,
            GatewayDbContext dbContext,
            ApiKeyHasher hasher,
            SensitiveDataProtector sensitiveDataProtector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return InvalidIdentity();
            }

            if (request.ExpiresAtUtc is not null && request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                return Results.BadRequest(new { error = "ExpiresAtUtc must be in the future." });
            }

            var secret = ApiKeyHasher.GenerateSecret();
            var credential = new ApiCredential(
                request.Name,
                ApiKeyHasher.GetPrefix(secret),
                hasher.Hash(secret),
                request.ExpiresAtUtc,
                identity.TenantId,
                identity.ObjectId,
                identity.PrincipalName);
            credential.SetSecretCiphertext(
                sensitiveDataProtector.Protect(secret, $"api-credential:{credential.Id}"));
            dbContext.ApiCredentials.Add(credential);
            AddAudit(dbContext, httpContext, identity, "credential.self_service.create", credential, new
            {
                credential.Name,
                credential.KeyPrefix,
                credential.ExpiresAtUtc
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            httpContext.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new
            {
                credential.Id,
                credential.Name,
                credential.KeyPrefix,
                credential.CreatedAtUtc,
                credential.ExpiresAtUtc,
                secret
            });
        });

        group.MapPost("/api-credentials/{id:guid}/rotate", async (
            Guid id,
            GatewayDbContext dbContext,
            ApiKeyHasher hasher,
            SensitiveDataProtector sensitiveDataProtector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return InvalidIdentity();
            }

            var credential = await FindOwnedCredentialAsync(dbContext, id, identity, cancellationToken);
            if (credential is null)
            {
                return Results.NotFound();
            }

            if (!credential.Enabled)
            {
                return Results.Conflict(new { error = "Revoked credentials cannot be rotated." });
            }

            var previousKeyPrefix = credential.KeyPrefix;
            var secret = ApiKeyHasher.GenerateSecret();
            credential.Rotate(ApiKeyHasher.GetPrefix(secret), hasher.Hash(secret));
            credential.SetSecretCiphertext(
                sensitiveDataProtector.Protect(secret, $"api-credential:{credential.Id}"));
            AddAudit(dbContext, httpContext, identity, "credential.self_service.rotate", credential, new
            {
                credential.Name,
                previousKeyPrefix,
                newKeyPrefix = credential.KeyPrefix
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            httpContext.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new
            {
                credential.Id,
                credential.Name,
                credential.KeyPrefix,
                credential.Enabled,
                credential.CreatedAtUtc,
                credential.ExpiresAtUtc,
                credential.LastUsedAtUtc,
                secret
            });
        });

        group.MapPost("/api-credentials/{id:guid}/revoke", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return InvalidIdentity();
            }

            var credential = await FindOwnedCredentialAsync(dbContext, id, identity, cancellationToken);
            if (credential is null)
            {
                return Results.NotFound();
            }

            credential.Revoke();
            AddAudit(dbContext, httpContext, identity, "credential.self_service.revoke", credential, new
            {
                credential.Name,
                credential.KeyPrefix
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/rate-limits", async (
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return InvalidIdentity();
            }

            var tenantId = identity.TenantId.ToUpperInvariant();
            var objectId = identity.ObjectId.ToUpperInvariant();
            var policies = await dbContext.UserRateLimitPolicies.AsNoTracking()
                .Where(item => item.OwnerTenantId.ToUpper() == tenantId && item.OwnerObjectId.ToUpper() == objectId)
                .OrderBy(item => item.LogicalModel)
                .Select(item => new
                {
                    item.Id,
                    item.LogicalModel,
                    item.RequestsPerWindow,
                    item.WindowSeconds,
                    item.Enabled,
                    item.UpdatedAtUtc
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(policies);
        });

        group.MapGet("/usage", async (
            int? days,
            GatewayDbContext dbContext,
            UsageReportingReader reader,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!EntraUserIdentityResolver.TryResolve(httpContext.User, out var identity))
            {
                return InvalidIdentity();
            }

            var credentialIds = await dbContext.ApiCredentials.AsNoTracking()
                .Where(item => item.OwnerTenantId == identity.TenantId && item.OwnerObjectId == identity.ObjectId)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            var credentialSet = credentialIds.ToHashSet();
            var report = await reader.ReadAsync(days ?? 30, cancellationToken);
            var credentials = report.Credentials
                .Where(item => credentialSet.Contains(item.ApiCredentialId))
                .ToArray();

            return Results.Ok(new
            {
                report.WindowDays,
                report.SinceUtc,
                report.WindowGranularity,
                report.HistoricalRollupsUsed,
                requestCount = credentials.Sum(item => item.RequestCount),
                errorCount = credentials.Sum(item => item.ErrorCount),
                inputTokens = credentials.Sum(item => item.InputTokens),
                outputTokens = credentials.Sum(item => item.OutputTokens),
                totalTokens = credentials.Sum(item => item.TotalTokens),
                rateLimitedRequests = credentials.Sum(item => item.RateLimitedRequests),
                credentials
            });
        });

        return endpoints;
    }

    private static async Task<ApiCredential?> FindOwnedCredentialAsync(
        GatewayDbContext dbContext,
        Guid id,
        EntraUserIdentity identity,
        CancellationToken cancellationToken)
        => await dbContext.ApiCredentials.SingleOrDefaultAsync(
            item => item.Id == id && item.OwnerTenantId == identity.TenantId && item.OwnerObjectId == identity.ObjectId,
            cancellationToken);

    private static IResult InvalidIdentity()
        => Results.Json(new
        {
            error = "entra_identity_missing",
            message = "The authenticated token must contain stable Entra tenant (tid) and object (oid) claims."
        }, statusCode: StatusCodes.Status403Forbidden);

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        EntraUserIdentity identity,
        string action,
        ApiCredential credential,
        object details)
    {
        var detailsJson = JsonSerializer.Serialize(new
        {
            ownerTenantId = identity.TenantId,
            ownerObjectId = identity.ObjectId,
            details
        });
        dbContext.AuditEvents.Add(new AuditEvent(
            identity.AuditActor,
            action,
            "api_credential",
            credential.Id.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            detailsJson));
    }

    public sealed record CreatePersonalApiCredentialRequest(string Name, DateTimeOffset? ExpiresAtUtc = null);
}
