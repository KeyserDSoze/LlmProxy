using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Observability;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.Security;

public sealed class InferenceApiKeyMiddleware(RequestDelegate next)
{
    public const string ApiCredentialIdItem = "LlmProxy.ApiCredentialId";
    public const string UsageGroupIdItem = "LlmProxy.UsageGroupId";
    public const string OwnerTenantIdItem = "LlmProxy.OwnerTenantId";
    public const string OwnerObjectIdItem = "LlmProxy.OwnerObjectId";
    public const string EnforceCallerGovernanceItem = "LlmProxy.EnforceCallerGovernance";

    public async Task InvokeAsync(
        HttpContext context,
        ApiKeyHasher apiKeyHasher,
        IApiCredentialCache credentialCache,
        ICredentialUsageSink credentialUsageSink)
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        ApiCredentialSnapshot credential;
        var now = DateTimeOffset.UtcNow;

        using (var activity = LlmProxyActivity.Start("llmproxy.auth"))
        {
            activity?.SetTag("llmproxy.auth.scheme", "bearer");

            if (!context.Request.Headers.TryGetValue("Authorization", out var authorization) ||
                !authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                activity?.SetTag("llmproxy.auth.result", "missing");
                LlmProxyActivity.MarkError(activity, "invalid_api_key");
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "A bearer API key is required.");
                return;
            }

            var supplied = authorization.ToString()["Bearer ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(supplied))
            {
                activity?.SetTag("llmproxy.auth.result", "invalid");
                LlmProxyActivity.MarkError(activity, "invalid_api_key");
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "The supplied API key is invalid.");
                return;
            }

            var hash = apiKeyHasher.Hash(supplied);
            if (!credentialCache.TryGetUsableByHash(hash, now, out credential))
            {
                activity?.SetTag("llmproxy.auth.result", "rejected");
                LlmProxyActivity.MarkError(activity, "invalid_api_key");
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "The supplied API key is invalid, revoked or expired.");
                return;
            }

            context.Items[ApiCredentialIdItem] = credential.Id;
            if (credential.UsageGroupId is Guid usageGroupId)
            {
                context.Items[UsageGroupIdItem] = usageGroupId;
            }
            if (!string.IsNullOrWhiteSpace(credential.OwnerTenantId))
            {
                context.Items[OwnerTenantIdItem] = credential.OwnerTenantId;
            }
            if (!string.IsNullOrWhiteSpace(credential.OwnerObjectId))
            {
                context.Items[OwnerObjectIdItem] = credential.OwnerObjectId;
            }
            context.Items[EnforceCallerGovernanceItem] = credential.EnforceCallerGovernance;

            activity?.SetTag("llmproxy.auth.result", "allowed");
            activity?.SetTag("llmproxy.auth.personal", credential.OwnerObjectId is not null);
            activity?.SetTag("llmproxy.auth.caller_governance", credential.EnforceCallerGovernance);
            LlmProxyActivity.SetGuid(activity, "llmproxy.api_credential.id", credential.Id);
            LlmProxyActivity.SetGuid(activity, "llmproxy.usage_group.id", credential.UsageGroupId);
        }

        credentialUsageSink.RecordUsage(credential.Id, now);
        await next(context);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type = "authentication_error",
                code
            }
        });
    }
}
