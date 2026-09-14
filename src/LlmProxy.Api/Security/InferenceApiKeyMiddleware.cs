using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Security;

public sealed class InferenceApiKeyMiddleware(RequestDelegate next)
{
    public const string ApiCredentialIdItem = "LlmProxy.ApiCredentialId";
    public const string UsageGroupIdItem = "LlmProxy.UsageGroupId";

    public async Task InvokeAsync(HttpContext context, GatewayDbContext dbContext, ApiKeyHasher apiKeyHasher)
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out var authorization) ||
            !authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "A bearer API key is required.");
            return;
        }

        var supplied = authorization.ToString()["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "The supplied API key is invalid.");
            return;
        }

        var hash = apiKeyHasher.Hash(supplied);
        var credential = await dbContext.ApiCredentials.SingleOrDefaultAsync(item => item.KeyHash == hash, context.RequestAborted);
        var now = DateTimeOffset.UtcNow;

        if (credential is null || !credential.IsUsable(now))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "The supplied API key is invalid, revoked or expired.");
            return;
        }

        context.Items[ApiCredentialIdItem] = credential.Id;
        if (credential.UsageGroupId is Guid usageGroupId)
        {
            context.Items[UsageGroupIdItem] = usageGroupId;
        }

        var previousLastUsed = credential.LastUsedAtUtc;
        credential.Touch(now);
        if (credential.LastUsedAtUtc != previousLastUsed)
        {
            await dbContext.SaveChangesAsync(context.RequestAborted);
        }

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
