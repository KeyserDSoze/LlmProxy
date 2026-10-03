using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.Admin;

public static class CredentialRotationAdminEndpoints
{
    public static IEndpointRouteBuilder MapCredentialRotationAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var endpoint = endpoints.MapPost("/api/admin/api-credentials/{id:guid}/rotate", async (
            Guid id,
            GatewayDbContext dbContext,
            ApiKeyHasher hasher,
            SensitiveDataProtector sensitiveDataProtector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var credential = await dbContext.ApiCredentials.FindAsync([id], cancellationToken);
            if (credential is null)
            {
                return Results.NotFound();
            }

            if (!credential.Enabled)
            {
                return Results.Conflict(new { error = "Revoked credentials cannot be rotated." });
            }

            var previousKeyPrefix = credential.KeyPrefix;
            var secret = ApiKeyHasher.GenerateSecret(credential.IsPersonal ? "lp_usr_" : "lp_org_");
            credential.Rotate(ApiKeyHasher.GetPrefix(secret), hasher.Hash(secret));
            credential.SetSecretCiphertext(
                sensitiveDataProtector.Protect(secret, $"api-credential:{credential.Id}"));

            var actor = ResolveActor(httpContext);
            var sourceIp = httpContext.Connection.RemoteIpAddress?.ToString();
            var detailsJson = JsonSerializer.Serialize(new
            {
                credential.Name,
                previousKeyPrefix,
                newKeyPrefix = credential.KeyPrefix,
                credential.ExpiresAtUtc,
                credential.UsageGroupId
            });
            dbContext.AuditEvents.Add(new AuditEvent(
                actor,
                "credential.rotate",
                "api_credential",
                credential.Id.ToString(),
                sourceIp,
                detailsJson));

            await dbContext.SaveChangesAsync(cancellationToken);

            // The raw secret is persisted only as application-encrypted ciphertext so administrators can recover it later.
            // Prevent intermediaries and browsers from treating this control-plane response as cacheable.
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
                credential.UsageGroupId,
                secretAvailable = true,
                secret
            });
        });

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminWrite");
        }

        return endpoints;
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
}
