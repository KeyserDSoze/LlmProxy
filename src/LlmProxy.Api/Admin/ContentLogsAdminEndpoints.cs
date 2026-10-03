using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Retention;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class ContentLogsAdminEndpoints
{
    public static IEndpointRouteBuilder MapContentLogsAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/content-logs");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminWrite");
        }

        group.MapGet("", async (
            int? take,
            string? surface,
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var size = Math.Clamp(take ?? 100, 1, 500);
            var query = dbContext.InferenceContentLogs.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(surface))
            {
                var normalizedSurface = surface.Trim();
                query = query.Where(item => item.Surface == normalizedSurface);
            }

            var rows = await query
                .OrderByDescending(item => item.StartedAtUtc)
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

            return Results.Ok(rows);
        });

        group.MapGet("/query", async (
            int? page,
            int? pageSize,
            string? surface,
            string? model,
            Guid? apiCredentialId,
            string? ownerTenantId,
            string? ownerObjectId,
            string? status,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            Guid? requestId,
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var currentPage = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 100);
            var query = dbContext.InferenceContentLogs.AsNoTracking().AsQueryable();

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
                query = query.Where(item => item.ApiCredentialId == selectedCredentialId);
            }

            if (!string.IsNullOrWhiteSpace(ownerTenantId) || !string.IsNullOrWhiteSpace(ownerObjectId))
            {
                var credentials = dbContext.ApiCredentials.AsNoTracking().AsQueryable();
                if (!string.IsNullOrWhiteSpace(ownerTenantId))
                {
                    var tenantId = ownerTenantId.Trim();
                    credentials = credentials.Where(item => item.OwnerTenantId == tenantId);
                }

                if (!string.IsNullOrWhiteSpace(ownerObjectId))
                {
                    var objectId = ownerObjectId.Trim();
                    credentials = credentials.Where(item => item.OwnerObjectId == objectId);
                }

                var credentialIds = credentials.Select(item => item.Id);
                query = query.Where(item => item.ApiCredentialId != null && credentialIds.Contains(item.ApiCredentialId.Value));
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

        group.MapGet("/{id:long}", async (
            long id,
            GatewayDbContext dbContext,
            SensitiveDataProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var row = await dbContext.InferenceContentLogs
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
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

        group.MapGet("/settings", async (
            ContentLogRetentionService service,
            CancellationToken cancellationToken) =>
        {
            var settings = await service.GetSettingsAsync(cancellationToken);
            return Results.Ok(new
            {
                settings.RetentionDays,
                settings.UpdatedAtUtc,
                minimumRetentionDays = ContentLogRetentionService.MinimumRetentionDays,
                maximumRetentionDays = ContentLogRetentionService.MaximumRetentionDays,
                cleanupIntervalHours = ContentLogRetentionService.CleanupIntervalHours
            });
        });

        group.MapPut("/settings", async (
            UpdateContentLogSettingsRequest request,
            ContentLogRetentionService service,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (request.RetentionDays is < ContentLogRetentionService.MinimumRetentionDays or > ContentLogRetentionService.MaximumRetentionDays)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_retention",
                    message = $"RetentionDays must be between {ContentLogRetentionService.MinimumRetentionDays} and {ContentLogRetentionService.MaximumRetentionDays}."
                });
            }

            var before = await service.GetSettingsAsync(cancellationToken);
            var previousDays = before.RetentionDays;
            var updated = await service.UpdateRetentionDaysAsync(request.RetentionDays, cancellationToken);
            AddAudit(dbContext, httpContext, "content_log.retention.update", new
            {
                previousRetentionDays = previousDays,
                retentionDays = updated.RetentionDays
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                updated.RetentionDays,
                updated.UpdatedAtUtc,
                minimumRetentionDays = ContentLogRetentionService.MinimumRetentionDays,
                maximumRetentionDays = ContentLogRetentionService.MaximumRetentionDays,
                cleanupIntervalHours = ContentLogRetentionService.CleanupIntervalHours
            });
        });

        group.MapPost("/retention/run", async (
            ContentLogRetentionService service,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RunAsync(cancellationToken: cancellationToken);
            AddAudit(dbContext, httpContext, "content_log.retention.cleanup.run", result);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(result);
        });

        return endpoints;
    }

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        object details)
    {
        dbContext.AuditEvents.Add(new AuditEvent(
            ResolveActor(httpContext),
            action,
            "content_log",
            "settings",
            httpContext.Connection.RemoteIpAddress?.ToString(),
            JsonSerializer.Serialize(details)));
    }

    private static string ResolveActor(HttpContext httpContext)
        => httpContext.User.Identity?.IsAuthenticated != true
            ? "local-admin"
            : httpContext.User.FindFirstValue("preferred_username")
              ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
              ?? httpContext.User.Identity?.Name
              ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? "authenticated-admin";

    public sealed record UpdateContentLogSettingsRequest(int RetentionDays);
}
