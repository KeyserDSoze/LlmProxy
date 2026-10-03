using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Application.Governance;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Governance;
using LlmProxy.Infrastructure.Governance;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class UsageGovernanceEndpoints
{
    public static IEndpointRouteBuilder MapUsageGovernanceEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("/usage-groups", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var rows = await dbContext.UsageGroups.AsNoTracking()
                .OrderBy(item => item.Name)
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.Description,
                    item.CreatedAtUtc,
                    item.UpdatedAtUtc,
                    credentialCount = dbContext.ApiCredentials.Count(credential => credential.UsageGroupId == item.Id),
                    userCount = dbContext.PlatformUsers.Count(user => user.UsageGroupId == item.Id)
                })
                .ToListAsync(cancellationToken);
            return Results.Ok(rows);
        });

        var createUsageGroup = group.MapPost("/usage-groups", async (
            UpsertUsageGroupRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var normalizedName = request.Name.Trim();
            if (await dbContext.UsageGroups.AnyAsync(item => item.Name == normalizedName, cancellationToken))
            {
                return Results.Conflict(new { error = "A usage group with this name already exists." });
            }

            var usageGroup = new UsageGroup(request.Name, request.Description);
            dbContext.UsageGroups.Add(usageGroup);
            AddAudit(dbContext, httpContext, "usage_group.create", "usage_group", usageGroup.Id.ToString(), new
            {
                usageGroup.Name,
                usageGroup.Description
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/usage-groups/{usageGroup.Id}", usageGroup);
        });

        var updateUsageGroup = group.MapPut("/usage-groups/{id:guid}", async (
            Guid id,
            UpsertUsageGroupRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var usageGroup = await dbContext.UsageGroups.FindAsync([id], cancellationToken);
            if (usageGroup is null)
            {
                return Results.NotFound();
            }

            var normalizedName = request.Name.Trim();
            if (await dbContext.UsageGroups.AnyAsync(item => item.Id != id && item.Name == normalizedName, cancellationToken))
            {
                return Results.Conflict(new { error = "A usage group with this name already exists." });
            }

            var before = new { usageGroup.Name, usageGroup.Description };
            usageGroup.Update(request.Name, request.Description);
            AddAudit(dbContext, httpContext, "usage_group.update", "usage_group", usageGroup.Id.ToString(), new
            {
                before,
                after = new { usageGroup.Name, usageGroup.Description }
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(usageGroup);
        });

        var assignCredentialGroup = group.MapPut("/api-credentials/{id:guid}/usage-group", async (
            Guid id,
            AssignUsageGroupRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var credential = await dbContext.ApiCredentials.FindAsync([id], cancellationToken);
            if (credential is null)
            {
                return Results.NotFound();
            }

            var exists = await dbContext.UsageGroups.AnyAsync(item => item.Id == request.UsageGroupId, cancellationToken);
            if (!exists)
            {
                return Results.BadRequest(new { error = "UsageGroupId must reference an existing usage group." });
            }

            var previous = credential.UsageGroupId;
            credential.AssignUsageGroup(request.UsageGroupId);
            AddAudit(dbContext, httpContext, "credential.usage_group.assign", "api_credential", credential.Id.ToString(), new
            {
                previousUsageGroupId = previous,
                usageGroupId = credential.UsageGroupId
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        var clearCredentialGroup = group.MapDelete("/api-credentials/{id:guid}/usage-group", async (
            Guid id,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var credential = await dbContext.ApiCredentials.FindAsync([id], cancellationToken);
            if (credential is null)
            {
                return Results.NotFound();
            }

            var previous = credential.UsageGroupId;
            credential.ClearUsageGroup();
            AddAudit(dbContext, httpContext, "credential.usage_group.clear", "api_credential", credential.Id.ToString(), new
            {
                previousUsageGroupId = previous
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/rate-limits", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var policies = await dbContext.RateLimitPolicies.AsNoTracking()
                .OrderBy(policy => policy.ApiCredentialId)
                .ThenBy(policy => policy.LogicalModel)
                .ToListAsync(cancellationToken);
            var credentials = await dbContext.ApiCredentials.AsNoTracking()
                .Select(credential => new { credential.Id, credential.Name, credential.KeyPrefix })
                .ToDictionaryAsync(credential => credential.Id, cancellationToken);

            return Results.Ok(policies.Select(policy =>
            {
                credentials.TryGetValue(policy.ApiCredentialId, out var credential);
                return new
                {
                    policy.Id,
                    policy.ApiCredentialId,
                    credentialName = credential?.Name,
                    keyPrefix = credential?.KeyPrefix,
                    policy.LogicalModel,
                    policy.RequestsPerWindow,
                    policy.WindowSeconds,
                    policy.OutputTokensPerWindow,
                    policy.MaxOutputTokensPerRequest,
                    policy.Enabled,
                    policy.CreatedAtUtc,
                    policy.UpdatedAtUtc
                };
            }));
        });

        var createRateLimit = group.MapPost("/rate-limits", async (
            CreateRateLimitRequest request,
            GatewayDbContext dbContext,
            RequestRateLimiter rateLimiter,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!await dbContext.ApiCredentials.AnyAsync(item => item.Id == request.ApiCredentialId, cancellationToken))
            {
                return Results.BadRequest(new { error = "ApiCredentialId must reference an existing credential." });
            }

            var logicalModel = NormalizeLogicalModel(request.LogicalModel);
            if (logicalModel is not null && !await dbContext.Models.AnyAsync(model => model.PublicName == logicalModel, cancellationToken))
            {
                return Results.BadRequest(new { error = "LogicalModel must reference an existing public model." });
            }

            if (await dbContext.RateLimitPolicies.AnyAsync(
                    policy => policy.ApiCredentialId == request.ApiCredentialId && policy.LogicalModel == logicalModel,
                    cancellationToken))
            {
                return Results.Conflict(new { error = "A rate-limit policy already exists for this credential/model scope." });
            }

            var policy = new RateLimitPolicy(
                request.ApiCredentialId,
                logicalModel,
                request.RequestsPerWindow,
                request.WindowSeconds,
                request.Enabled,
                request.OutputTokensPerWindow,
                request.MaxOutputTokensPerRequest);
            dbContext.RateLimitPolicies.Add(policy);
            AddAudit(dbContext, httpContext, "rate_limit.create", "rate_limit_policy", policy.Id.ToString(), new
            {
                policy.ApiCredentialId,
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds,
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest,
                policy.Enabled
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await PublishRateLimitsAsync(dbContext, rateLimiter, cancellationToken);
            return Results.Created($"/api/admin/rate-limits/{policy.Id}", policy);
        });

        var updateRateLimit = group.MapPut("/rate-limits/{id:guid}", async (
            Guid id,
            UpdateRateLimitRequest request,
            GatewayDbContext dbContext,
            RequestRateLimiter rateLimiter,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.RateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var logicalModel = NormalizeLogicalModel(request.LogicalModel);
            if (logicalModel is not null && !await dbContext.Models.AnyAsync(model => model.PublicName == logicalModel, cancellationToken))
            {
                return Results.BadRequest(new { error = "LogicalModel must reference an existing public model." });
            }

            if (await dbContext.RateLimitPolicies.AnyAsync(
                    item => item.Id != id && item.ApiCredentialId == policy.ApiCredentialId && item.LogicalModel == logicalModel,
                    cancellationToken))
            {
                return Results.Conflict(new { error = "A rate-limit policy already exists for this credential/model scope." });
            }

            var before = new
            {
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds,
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest,
                policy.Enabled
            };
            policy.Update(logicalModel, request.RequestsPerWindow, request.WindowSeconds, request.Enabled);
            if (request.OutputTokensPerWindow.HasValue || request.MaxOutputTokensPerRequest.HasValue)
            {
                if (!request.OutputTokensPerWindow.HasValue || !request.MaxOutputTokensPerRequest.HasValue)
                {
                    return Results.BadRequest(new { error = "OutputTokensPerWindow and MaxOutputTokensPerRequest must both be supplied when changing the token budget." });
                }

                policy.SetOutputTokenBudget(request.OutputTokensPerWindow.Value, request.MaxOutputTokensPerRequest.Value);
            }
            AddAudit(dbContext, httpContext, "rate_limit.update", "rate_limit_policy", policy.Id.ToString(), new
            {
                before,
                after = new
                {
                    policy.LogicalModel,
                    policy.RequestsPerWindow,
                    policy.WindowSeconds,
                    policy.OutputTokensPerWindow,
                    policy.MaxOutputTokensPerRequest,
                    policy.Enabled
                }
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await PublishRateLimitsAsync(dbContext, rateLimiter, cancellationToken);
            return Results.Ok(policy);
        });

        var deleteRateLimit = group.MapDelete("/rate-limits/{id:guid}", async (
            Guid id,
            GatewayDbContext dbContext,
            RequestRateLimiter rateLimiter,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.RateLimitPolicies.FindAsync([id], cancellationToken);
            if (policy is null)
            {
                return Results.NotFound();
            }

            AddAudit(dbContext, httpContext, "rate_limit.delete", "rate_limit_policy", policy.Id.ToString(), new
            {
                policy.ApiCredentialId,
                policy.LogicalModel,
                policy.RequestsPerWindow,
                policy.WindowSeconds,
                policy.OutputTokensPerWindow,
                policy.MaxOutputTokensPerRequest
            });
            dbContext.RateLimitPolicies.Remove(policy);
            await dbContext.SaveChangesAsync(cancellationToken);
            await PublishRateLimitsAsync(dbContext, rateLimiter, cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/usage/summary", async (
            int? days,
            UsageReportingReader reader,
            CancellationToken cancellationToken) =>
            Results.Ok(await reader.ReadAsync(days ?? 30, cancellationToken)));

        group.MapGet("/usage/groups", async (
            int? days,
            UsageReportingReader reader,
            CancellationToken cancellationToken) =>
        {
            var report = await reader.ReadAsync(days ?? 30, cancellationToken);
            return Results.Ok(report.Groups);
        });

        group.MapGet("/usage/credentials", async (
            int? days,
            UsageReportingReader reader,
            CancellationToken cancellationToken) =>
        {
            var report = await reader.ReadAsync(days ?? 30, cancellationToken);
            return Results.Ok(report.Credentials);
        });

        group.MapGet("/usage/users", async (
            int? days,
            UsageReportingReader reader,
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var report = await reader.ReadAsync(days ?? 30, cancellationToken);
            var owners = await dbContext.ApiCredentials.AsNoTracking()
                .Where(item => item.OwnerTenantId != null && item.OwnerObjectId != null)
                .Select(item => new
                {
                    item.Id,
                    TenantId = item.OwnerTenantId!,
                    ObjectId = item.OwnerObjectId!
                })
                .ToDictionaryAsync(item => item.Id, cancellationToken);

            var users = await dbContext.PlatformUsers.AsNoTracking()
                .Select(item => new
                {
                    item.TenantId,
                    item.ObjectId,
                    item.PrincipalName,
                    item.DisplayName,
                    item.UsageGroupId
                })
                .ToListAsync(cancellationToken);
            var userLookup = users.ToDictionary(
                item => $"{item.TenantId}|{item.ObjectId}",
                StringComparer.OrdinalIgnoreCase);

            var rows = report.Credentials
                .Where(item => owners.ContainsKey(item.ApiCredentialId))
                .GroupBy(item =>
                {
                    var owner = owners[item.ApiCredentialId];
                    return $"{owner.TenantId}|{owner.ObjectId}";
                }, StringComparer.OrdinalIgnoreCase)
                .Select(grouping =>
                {
                    var firstCredentialId = grouping.First().ApiCredentialId;
                    var owner = owners[firstCredentialId];
                    userLookup.TryGetValue(grouping.Key, out var user);
                    return new
                    {
                        tenantId = owner.TenantId,
                        objectId = owner.ObjectId,
                        principalName = user?.PrincipalName,
                        displayName = user?.DisplayName,
                        usageGroupId = user?.UsageGroupId,
                        requestCount = grouping.Sum(item => item.RequestCount),
                        errorCount = grouping.Sum(item => item.ErrorCount),
                        inputTokens = grouping.Sum(item => item.InputTokens),
                        outputTokens = grouping.Sum(item => item.OutputTokens),
                        totalTokens = grouping.Sum(item => item.TotalTokens),
                        rateLimitedRequests = grouping.Sum(item => item.RateLimitedRequests)
                    };
                })
                .OrderByDescending(item => item.requestCount)
                .ToArray();

            return Results.Ok(rows);
        });

        group.MapGet("/usage/models", async (
            int? days,
            UsageReportingReader reader,
            CancellationToken cancellationToken) =>
        {
            var report = await reader.ReadAsync(days ?? 30, cancellationToken);
            return Results.Ok(report.Models);
        });

        if (entraEnabled)
        {
            foreach (var endpoint in new[]
            {
                createUsageGroup,
                updateUsageGroup,
                assignCredentialGroup,
                clearCredentialGroup,
                createRateLimit,
                updateRateLimit,
                deleteRateLimit
            })
            {
                endpoint.RequireAuthorization("AdminWrite");
            }
        }

        return endpoints;
    }

    private static string? NormalizeLogicalModel(string? logicalModel) =>
        string.IsNullOrWhiteSpace(logicalModel) ? null : logicalModel.Trim();

    private static async Task PublishRateLimitsAsync(
        GatewayDbContext dbContext,
        RequestRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        var credentialPolicies = (await dbContext.RateLimitPolicies.AsNoTracking().ToListAsync(cancellationToken))
            .Select(RateLimitPolicyRuntimeStateInterceptor.ToSnapshot);
        var userPolicies = (await dbContext.UserRateLimitPolicies.AsNoTracking().ToListAsync(cancellationToken))
            .Select(RateLimitPolicyRuntimeStateInterceptor.ToSnapshot);
        var groupPolicies = (await dbContext.UsageGroupRateLimitPolicies.AsNoTracking().ToListAsync(cancellationToken))
            .Select(RateLimitPolicyRuntimeStateInterceptor.ToSnapshot);

        rateLimiter.ReplacePolicies(credentialPolicies.Concat(userPolicies).Concat(groupPolicies));
    }

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        string entityType,
        string entityId,
        object? details = null)
    {
        var actor = ResolveActor(httpContext);
        var sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var detailsJson = details is null ? null : JsonSerializer.Serialize(details);
        dbContext.AuditEvents.Add(new AuditEvent(actor, action, entityType, entityId, sourceIp, detailsJson));
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

    public sealed record UpsertUsageGroupRequest(string Name, string? Description = null);
    public sealed record AssignUsageGroupRequest(Guid UsageGroupId);
    public sealed record CreateRateLimitRequest(
        Guid ApiCredentialId,
        string? LogicalModel,
        int RequestsPerWindow,
        int WindowSeconds = 60,
        bool Enabled = true,
        int? OutputTokensPerWindow = null,
        int? MaxOutputTokensPerRequest = null);
    public sealed record UpdateRateLimitRequest(
        string? LogicalModel,
        int RequestsPerWindow,
        int WindowSeconds = 60,
        bool Enabled = true,
        int? OutputTokensPerWindow = null,
        int? MaxOutputTokensPerRequest = null);
}
