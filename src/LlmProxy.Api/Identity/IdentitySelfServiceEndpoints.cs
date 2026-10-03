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
                    item.UsageGroupId,
                    item.EnforceCallerGovernance
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

            var tenantLookup = identity.TenantId.ToUpperInvariant();
            var objectLookup = identity.ObjectId.ToUpperInvariant();
            var userGroupId = await dbContext.PlatformUsers.AsNoTracking()
                .Where(item => item.TenantId.ToUpper() == tenantLookup && item.ObjectId.ToUpper() == objectLookup)
                .Select(item => item.UsageGroupId)
                .SingleAsync(cancellationToken);

            var secret = ApiKeyHasher.GenerateSecret("lp_usr_");
            var credential = new ApiCredential(
                request.Name,
                ApiKeyHasher.GetPrefix(secret),
                hasher.Hash(secret),
                request.ExpiresAtUtc,
                identity.TenantId,
                identity.ObjectId,
                identity.PrincipalName);
            if (userGroupId is Guid groupId)
            {
                credential.AssignUsageGroup(groupId);
            }
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
            var secret = ApiKeyHasher.GenerateSecret("lp_usr_");
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

        group.MapGet("/requests", async (
            int? take,
            GatewayDbContext dbContext,
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
            var size = Math.Clamp(take ?? 50, 1, 200);

            var requests = await dbContext.RequestMetrics.AsNoTracking()
                .Where(item => item.ApiCredentialId != null && credentialIds.Contains(item.ApiCredentialId.Value))
                .OrderByDescending(item => item.StartedAtUtc)
                .Take(size)
                .Select(item => new
                {
                    item.RequestId,
                    item.StartedAtUtc,
                    item.LogicalModel,
                    item.Surface,
                    item.StatusCode,
                    item.DurationMilliseconds,
                    item.IsStreaming,
                    item.TimeToFirstByteMilliseconds,
                    item.InputTokens,
                    item.OutputTokens,
                    item.TotalTokens,
                    item.ErrorCode,
                    item.ApiCredentialId
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(requests);
        });

        group.MapGet("/content-logs", async (
            int? page,
            int? pageSize,
            string? surface,
            string? model,
            Guid? apiCredentialId,
            string? status,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            Guid? requestId,
            GatewayDbContext dbContext,
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
            var currentPage = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 100);
            var query = dbContext.InferenceContentLogs.AsNoTracking()
                .Where(item => item.ApiCredentialId != null && credentialIds.Contains(item.ApiCredentialId.Value));

            if (!string.IsNullOrWhiteSpace(surface))
            {
                var normalizedSurface = surface.Trim();
                query = query.Where(item => item.Surface == normalizedSurface);
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                var normalizedModel = model.Trim().ToLower();
                query = query.Where(item => item.LogicalModel != null && item.LogicalModel.ToLower().Contains(normalizedModel));
            }

            if (apiCredentialId is Guid selectedCredentialId)
            {
                if (!credentialIds.Contains(selectedCredentialId))
                {
                    return Results.Ok(new
                    {
                        items = Array.Empty<object>(),
                        total = 0,
                        page = currentPage,
                        pageSize = size
                    });
                }

                query = query.Where(item => item.ApiCredentialId == selectedCredentialId);
            }

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(item => item.StatusCode >= 200 && item.StatusCode < 300);
            }
            else if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(item => item.StatusCode < 200 || item.StatusCode >= 300);
            }
            else if (int.TryParse(status, out var exactStatusCode))
            {
                query = query.Where(item => item.StatusCode == exactStatusCode);
            }

            if (fromUtc is not null)
            {
                query = query.Where(item => item.StartedAtUtc >= fromUtc.Value);
            }

            if (toUtc is not null)
            {
                query = query.Where(item => item.StartedAtUtc <= toUtc.Value);
            }

            if (requestId is Guid selectedRequestId)
            {
                query = query.Where(item => item.RequestId == selectedRequestId);
            }

            var total = await query.CountAsync(cancellationToken);
            var rows = await query
                .OrderByDescending(item => item.StartedAtUtc)
                .Skip((currentPage - 1) * size)
                .Take(size)
                .Select(item => new
                {
                    item.Id,
                    item.RequestId,
                    item.StartedAtUtc,
                    item.CompletedAtUtc,
                    item.Surface,
                    item.Method,
                    item.Path,
                    item.LogicalModel,
                    item.ApiCredentialId,
                    item.StatusCode,
                    item.RequestContentType,
                    item.ResponseContentType
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(new
            {
                items = rows,
                total,
                page = currentPage,
                pageSize = size
            });
        });

        group.MapGet("/content-logs/{id:long}", async (
            long id,
            GatewayDbContext dbContext,
            SensitiveDataProtector protector,
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

            var row = await dbContext.InferenceContentLogs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == id &&
                            item.ApiCredentialId != null &&
                            credentialIds.Contains(item.ApiCredentialId.Value),
                    cancellationToken);
            if (row is null)
            {
                return Results.NotFound();
            }

            var metric = await dbContext.RequestMetrics
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == row.RequestId, cancellationToken);

            string requestBody;
            string responseBody;
            try
            {
                requestBody = protector.Unprotect(row.RequestBodyCiphertext, $"content-log:{row.RequestId}:request");
                responseBody = protector.Unprotect(row.ResponseBodyCiphertext, $"content-log:{row.RequestId}:response");
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Results.Problem(
                    "The content log could not be decrypted with the configured Authentication:ApiKeyPepper.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                row.Id,
                row.RequestId,
                row.StartedAtUtc,
                row.CompletedAtUtc,
                row.Surface,
                row.Method,
                row.Path,
                row.LogicalModel,
                row.ApiCredentialId,
                row.StatusCode,
                row.RequestContentType,
                row.ResponseContentType,
                requestBody,
                responseBody,
                deploymentId = metric?.DeploymentId,
                nodeId = metric?.NodeId,
                usageGroupId = metric?.UsageGroupId,
                attemptCount = metric?.AttemptCount,
                isStreaming = metric?.IsStreaming,
                timeToFirstByteMilliseconds = metric?.TimeToFirstByteMilliseconds,
                inputTokens = metric?.InputTokens,
                outputTokens = metric?.OutputTokens,
                totalTokens = metric?.TotalTokens,
                errorCode = metric?.ErrorCode
            });
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
            var user = await dbContext.PlatformUsers.AsNoTracking()
                .SingleAsync(
                    item => item.TenantId.ToUpper() == tenantId && item.ObjectId.ToUpper() == objectId,
                    cancellationToken);

            var userPolicies = await dbContext.UserRateLimitPolicies.AsNoTracking()
                .Where(item => item.OwnerTenantId.ToUpper() == tenantId && item.OwnerObjectId.ToUpper() == objectId)
                .OrderBy(item => item.LogicalModel)
                .ToListAsync(cancellationToken);

            var groupPolicies = user.UsageGroupId is Guid usageGroupId
                ? await dbContext.UsageGroupRateLimitPolicies.AsNoTracking()
                    .Where(item => item.UsageGroupId == usageGroupId)
                    .OrderBy(item => item.LogicalModel)
                    .ToListAsync(cancellationToken)
                : [];

            var groupName = user.UsageGroupId is Guid groupId
                ? await dbContext.UsageGroups.AsNoTracking()
                    .Where(item => item.Id == groupId)
                    .Select(item => item.Name)
                    .SingleOrDefaultAsync(cancellationToken)
                : null;

            var rows = new List<object>();
            rows.AddRange(userPolicies.Select(item => (object)new
            {
                item.Id,
                scope = "user",
                scopeName = identity.PrincipalName ?? identity.ObjectId,
                item.LogicalModel,
                item.RequestsPerWindow,
                item.WindowSeconds,
                item.OutputTokensPerWindow,
                item.MaxOutputTokensPerRequest,
                item.Enabled,
                item.UpdatedAtUtc
            }));
            rows.AddRange(groupPolicies.Select(item => (object)new
            {
                item.Id,
                scope = "group",
                scopeName = groupName ?? item.UsageGroupId.ToString(),
                item.LogicalModel,
                item.RequestsPerWindow,
                item.WindowSeconds,
                item.OutputTokensPerWindow,
                item.MaxOutputTokensPerRequest,
                item.Enabled,
                item.UpdatedAtUtc
            }));

            return Results.Ok(rows);
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
