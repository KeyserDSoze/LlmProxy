using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class ApiCredentialSecretAdminEndpoints
{
    public static IEndpointRouteBuilder MapApiCredentialSecretAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var endpoint = endpoints.MapGet("/api/admin/api-credentials/{id:guid}/secret", async (
            Guid id,
            GatewayDbContext dbContext,
            SensitiveDataProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var credential = await dbContext.ApiCredentials
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (credential is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(credential.SecretCiphertext))
            {
                return Results.Conflict(new
                {
                    error = "secret_not_recoverable",
                    message = "This credential predates encrypted secret retention. Rotate it once to make future administrator reveal/copy available."
                });
            }

            string secret;
            try
            {
                secret = protector.Unprotect(credential.SecretCiphertext, $"api-credential:{credential.Id}");
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Results.Problem(
                    "The credential secret could not be decrypted with the configured Authentication:ApiKeyPepper.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            dbContext.AuditEvents.Add(new AuditEvent(
                ResolveActor(httpContext),
                "credential.secret.reveal",
                "api_credential",
                credential.Id.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { credential.Name, credential.KeyPrefix })));
            await dbContext.SaveChangesAsync(cancellationToken);

            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                credential.Id,
                credential.Name,
                credential.KeyPrefix,
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
        => httpContext.User.Identity?.IsAuthenticated != true
            ? "local-admin"
            : httpContext.User.FindFirstValue("preferred_username")
              ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
              ?? httpContext.User.Identity?.Name
              ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? "authenticated-admin";
}
