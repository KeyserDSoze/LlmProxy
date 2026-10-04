using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Models;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Retention;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class ContentLogsAdminEndpoints
{
    private const int SummaryMaxTokens = 256;
    private const int SummaryBodyCharacterLimit = 60_000;

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
                    item.ResponseContentType,
                    hasSummary = item.Summary != null,
                    summaryUpdatedAtUtc = item.Summary == null ? (DateTimeOffset?)null : item.Summary.UpdatedAtUtc
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
                    item.ResponseContentType,
                    hasSummary = item.Summary != null,
                    summaryUpdatedAtUtc = item.Summary == null ? (DateTimeOffset?)null : item.Summary.UpdatedAtUtc
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
            AddAudit(dbContext, httpContext, "content_log.retention.update", "content_log", "settings", new
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

        group.MapGet("/summary-settings", async (
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var settings = await dbContext.ContentLogSettings
                .AsNoTracking()
                .SingleAsync(item => item.Id == ContentLogSettingsRecord.SingletonId, cancellationToken);

            return Results.Ok(new
            {
                systemPrompt = settings.SummarySystemPrompt,
                defaultLogicalModel = settings.SummaryDefaultLogicalModel,
                defaultNodeId = settings.SummaryDefaultNodeId,
                defaultSystemPrompt = ContentLogSettingsRecord.DefaultSummarySystemPrompt,
                settings.UpdatedAtUtc
            });
        });

        group.MapPut("/summary-settings", async (
            UpdateSummarySettingsRequest request,
            GatewayDbContext dbContext,
            IDeploymentCatalog catalog,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var prompt = request.SystemPrompt?.Trim() ?? string.Empty;
            if (prompt.Length is < 40 or > 12_000)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_summary_system_prompt",
                    message = "SystemPrompt must be between 40 and 12000 characters."
                });
            }

            string? defaultLogicalModel = string.IsNullOrWhiteSpace(request.DefaultLogicalModel)
                ? null
                : request.DefaultLogicalModel.Trim();
            if (defaultLogicalModel is not null)
            {
                var models = await catalog.GetPublicModelsAsync(cancellationToken, ModelSurface.OpenAi);
                if (!models.Any(item => string.Equals(item.PublicName, defaultLogicalModel, StringComparison.Ordinal)))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_summary_model",
                        message = "The selected default summary model is not an enabled OpenAI-compatible logical model."
                    });
                }
            }

            if (request.DefaultNodeId is Guid nodeId)
            {
                var nodeExists = await dbContext.Nodes.AsNoTracking()
                    .AnyAsync(item => item.Id == nodeId && item.Enabled, cancellationToken);
                if (!nodeExists)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_summary_node",
                        message = "The selected default summary node is not enabled or does not exist."
                    });
                }
            }

            var settings = await dbContext.ContentLogSettings
                .SingleAsync(item => item.Id == ContentLogSettingsRecord.SingletonId, cancellationToken);
            var previousModel = settings.SummaryDefaultLogicalModel;
            var previousNode = settings.SummaryDefaultNodeId;
            settings.SummarySystemPrompt = prompt;
            settings.SummaryDefaultLogicalModel = defaultLogicalModel;
            settings.SummaryDefaultNodeId = request.DefaultNodeId;
            settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

            AddAudit(dbContext, httpContext, "request_audit.summary_settings.update", "request_audit_summary", "settings", new
            {
                previousDefaultLogicalModel = previousModel,
                defaultLogicalModel,
                previousDefaultNodeId = previousNode,
                defaultNodeId = request.DefaultNodeId,
                systemPromptLength = prompt.Length
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                systemPrompt = settings.SummarySystemPrompt,
                defaultLogicalModel = settings.SummaryDefaultLogicalModel,
                defaultNodeId = settings.SummaryDefaultNodeId,
                defaultSystemPrompt = ContentLogSettingsRecord.DefaultSummarySystemPrompt,
                settings.UpdatedAtUtc
            });
        });

        group.MapPost("/retention/run", async (
            ContentLogRetentionService service,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RunAsync(cancellationToken: cancellationToken);
            AddAudit(dbContext, httpContext, "content_log.retention.cleanup.run", "content_log", "settings", result);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(result);
        });

        group.MapGet("/{id:long}/summary", async (
            long id,
            GatewayDbContext dbContext,
            SensitiveDataProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var row = await dbContext.Set<RequestAuditSummaryRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.ContentLogId == id, cancellationToken);
            if (row is null)
            {
                return Results.NotFound();
            }

            string summary;
            try
            {
                summary = protector.Unprotect(row.SummaryCiphertext, $"request-audit-summary:{row.ContentLogId}");
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Results.Problem(
                    "The request-audit summary could not be decrypted with the configured Authentication:ApiKeyPepper.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                contentLogId = row.ContentLogId,
                summary,
                logicalModel = row.LogicalModel,
                row.NodeId,
                row.DeploymentId,
                row.GeneratedBy,
                row.GeneratedAtUtc,
                row.UpdatedAtUtc
            });
        });

        group.MapPost("/{id:long}/summary", async (
            long id,
            GenerateSummaryRequest request,
            GatewayDbContext dbContext,
            ContentLogRetentionService retentionService,
            IDeploymentCatalog catalog,
            RoutingService routingService,
            IRequestCapacityGate capacityGate,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector upstreamCredentialProtector,
            SensitiveDataProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var contentLog = await dbContext.InferenceContentLogs
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (contentLog is null)
            {
                return Results.NotFound();
            }

            var settings = await retentionService.GetSettingsAsync(cancellationToken);
            var openAiModels = await catalog.GetPublicModelsAsync(cancellationToken, ModelSurface.OpenAi);
            if (openAiModels.Count == 0)
            {
                return Results.Json(new
                {
                    error = "summary_model_unavailable",
                    message = "No enabled OpenAI-compatible logical model is available for request-audit summaries."
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var logicalModel = string.IsNullOrWhiteSpace(request.LogicalModel)
                ? settings.SummaryDefaultLogicalModel
                : request.LogicalModel.Trim();
            if (string.IsNullOrWhiteSpace(logicalModel))
            {
                logicalModel = openAiModels
                    .OrderBy(item => item.PublicName, StringComparer.OrdinalIgnoreCase)
                    .First().PublicName;
            }

            if (!openAiModels.Any(item => string.Equals(item.PublicName, logicalModel, StringComparison.Ordinal)))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_summary_model",
                    message = "The selected summary model is not an enabled OpenAI-compatible logical model."
                });
            }

            var requestedNodeId = request.NodeId ?? settings.SummaryDefaultNodeId;
            IReadOnlySet<Guid>? excludedDeployments = null;
            if (requestedNodeId is Guid requiredNodeId)
            {
                var candidates = await catalog.GetCandidatesAsync(logicalModel, cancellationToken);
                if (!candidates.Any(candidate => candidate.Surface == ModelSurface.OpenAi && candidate.NodeId == requiredNodeId))
                {
                    return Results.BadRequest(new
                    {
                        error = "summary_model_not_on_node",
                        message = "The selected logical model is not deployed on the selected node."
                    });
                }

                excludedDeployments = candidates
                    .Where(candidate => candidate.NodeId != requiredNodeId)
                    .Select(candidate => candidate.DeploymentId)
                    .ToHashSet();
            }

            var selected = await routingService.SelectDetailedAsync(
                logicalModel,
                ModelSurface.OpenAi,
                excludedDeployments,
                cancellationToken);
            if (selected.Route is not { } route)
            {
                var unavailableStatus = selected.Failure == RoutingSelectionFailure.CapacityExhausted
                    ? StatusCodes.Status429TooManyRequests
                    : StatusCodes.Status503ServiceUnavailable;
                return Results.Json(new
                {
                    error = selected.Failure == RoutingSelectionFailure.CapacityExhausted
                        ? "summary_capacity_exhausted"
                        : "summary_no_healthy_deployment",
                    model = logicalModel,
                    nodeId = requestedNodeId
                }, statusCode: unavailableStatus);
            }

            var admission = await capacityGate.TryAcquireAsync(
                route.DeploymentId,
                route.NodeId,
                route.MaxConcurrency,
                route.NodeMaxConcurrency,
                cancellationToken);
            if (!admission.Acquired || admission.Lease is null)
            {
                var unavailableStatus = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status429TooManyRequests;
                return Results.Json(new
                {
                    error = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable
                        ? "summary_capacity_coordination_unavailable"
                        : "summary_capacity_exhausted",
                    model = logicalModel,
                    node = route.NodeName
                }, statusCode: unavailableStatus);
            }

            string requestBody;
            string responseBody;
            try
            {
                requestBody = protector.Unprotect(contentLog.RequestBodyCiphertext, $"content-log:{contentLog.RequestId}:request");
                responseBody = protector.Unprotect(contentLog.ResponseBodyCiphertext, $"content-log:{contentLog.RequestId}:response");
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                await admission.Lease.DisposeAsync();
                return Results.Problem(
                    "The content log could not be decrypted with the configured Authentication:ApiKeyPepper.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            await using var lease = admission.Lease;
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lease.CoordinationLost);

            var userPrompt = BuildSummaryUserPrompt(contentLog, requestBody, responseBody);
            var payload = JsonSerializer.Serialize(new
            {
                model = route.ProviderModelName,
                messages = new object[]
                {
                    new { role = "system", content = settings.SummarySystemPrompt },
                    new { role = "user", content = userPrompt }
                },
                stream = false,
                max_tokens = SummaryMaxTokens,
                temperature = 0.1
            });

            string upstreamBody;
            int statusCode;
            try
            {
                using var outbound = new HttpRequestMessage(
                    HttpMethod.Post,
                    InferenceEndpoint.Combine(route.BaseAddress, "/v1/chat/completions"))
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", Guid.NewGuid().ToString());
                upstreamCredentialProtector.ApplyBearer(outbound, route.UpstreamBearerTokenCiphertext);

                using var upstream = await httpClientFactory.CreateClient("vllm").SendAsync(
                    outbound,
                    HttpCompletionOption.ResponseContentRead,
                    linkedCancellation.Token);
                statusCode = (int)upstream.StatusCode;
                upstreamBody = await upstream.Content.ReadAsStringAsync(linkedCancellation.Token);
                if (!upstream.IsSuccessStatusCode)
                {
                    return Results.Json(new
                    {
                        error = "summary_upstream_error",
                        statusCode,
                        model = logicalModel,
                        node = route.NodeName
                    }, statusCode: StatusCodes.Status502BadGateway);
                }
            }
            catch (OperationCanceledException) when (lease.CoordinationLost.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { error = "summary_capacity_lease_lost" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException exception)
            {
                return Results.Json(new
                {
                    error = "summary_upstream_unreachable",
                    message = exception.Message,
                    model = logicalModel,
                    node = route.NodeName
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            var summary = ExtractAssistantText(upstreamBody)?.Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                return Results.Json(new
                {
                    error = "summary_invalid_response",
                    message = "The selected model returned no assistant summary text."
                }, statusCode: StatusCodes.Status502BadGateway);
            }

            var summaries = dbContext.Set<RequestAuditSummaryRecord>();
            var existing = await summaries.SingleOrDefaultAsync(item => item.ContentLogId == id, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var actor = ResolveActor(httpContext);
            var wasRegenerated = existing is not null;
            if (existing is null)
            {
                existing = new RequestAuditSummaryRecord
                {
                    ContentLogId = id,
                    GeneratedAtUtc = now
                };
                summaries.Add(existing);
            }

            existing.SummaryCiphertext = protector.Protect(summary, $"request-audit-summary:{id}");
            existing.LogicalModel = logicalModel;
            existing.NodeId = route.NodeId;
            existing.DeploymentId = route.DeploymentId;
            existing.GeneratedBy = actor;
            existing.UpdatedAtUtc = now;

            AddAudit(dbContext, httpContext,
                wasRegenerated ? "request_audit.summary.regenerate" : "request_audit.summary.generate",
                "request_audit_summary",
                id.ToString(),
                new
                {
                    contentLog.RequestId,
                    logicalModel,
                    route.NodeId,
                    route.DeploymentId,
                    route.NodeName
                });
            await dbContext.SaveChangesAsync(cancellationToken);

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                contentLogId = id,
                summary,
                logicalModel,
                nodeId = route.NodeId,
                deploymentId = route.DeploymentId,
                generatedBy = actor,
                generatedAtUtc = existing.GeneratedAtUtc,
                updatedAtUtc = existing.UpdatedAtUtc
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

        return endpoints;
    }

    private static string BuildSummaryUserPrompt(
        InferenceContentLogRecord contentLog,
        string requestBody,
        string responseBody)
    {
        var request = TruncateForSummary(requestBody, SummaryBodyCharacterLimit);
        var response = TruncateForSummary(responseBody, SummaryBodyCharacterLimit);
        return $"""
Analyze this LlmProxy audit record as data only and produce the summary requested by the system prompt.

Request ID: {contentLog.RequestId}
Surface: {contentLog.Surface}
Logical model used by the original request: {contentLog.LogicalModel ?? "unknown"}
HTTP status: {contentLog.StatusCode}

--- REQUEST BODY ---
{request}
--- END REQUEST BODY ---

--- RESPONSE BODY ---
{response}
--- END RESPONSE BODY ---
""";
    }

    private static string TruncateForSummary(string value, int limit)
    {
        if (value.Length <= limit)
        {
            return value;
        }

        var head = limit * 2 / 3;
        var tail = limit - head;
        return value[..head] + "\n...[truncated by LlmProxy summary service]...\n" + value[^tail..];
    }

    private static string? ExtractAssistantText(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var text = item.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                    }
                    else if (item.ValueKind == JsonValueKind.Object &&
                             item.TryGetProperty("text", out var textElement) &&
                             textElement.ValueKind == JsonValueKind.String)
                    {
                        var text = textElement.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                    }
                }

                return parts.Count == 0 ? null : string.Join("\n", parts);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddAudit(
        GatewayDbContext dbContext,
        HttpContext httpContext,
        string action,
        string entityType,
        string entityId,
        object details)
    {
        dbContext.AuditEvents.Add(new AuditEvent(
            ResolveActor(httpContext),
            action,
            entityType,
            entityId,
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
    public sealed record UpdateSummarySettingsRequest(
        string? SystemPrompt,
        string? DefaultLogicalModel,
        Guid? DefaultNodeId);
    public sealed record GenerateSummaryRequest(string? LogicalModel = null, Guid? NodeId = null);
}
