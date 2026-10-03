using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class CredentialGovernanceAdminEndpoints
{
    public static IEndpointRouteBuilder MapCredentialGovernanceAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var endpoint = endpoints.MapPut("/api/admin/api-credentials/{id:guid}/caller-governance", async (
            Guid id,
            UpdateCredentialGovernanceRequest request,
            GatewayDbContext dbContext,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var credential = await dbContext.ApiCredentials.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (credential is null)
            {
                return Results.NotFound();
            }

            if (credential.IsPersonal && !request.Enabled)
            {
                return Results.BadRequest(new
                {
                    error = "personal_governance_required",
                    message = "Personal API keys always participate in user/group caller governance."
                });
            }

            var previous = credential.EnforceCallerGovernance;
            credential.SetCallerGovernance(request.Enabled);

            var actor = httpContext.User.Identity?.IsAuthenticated == true
                ? httpContext.User.FindFirstValue("preferred_username")
                  ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
                  ?? httpContext.User.Identity?.Name
                  ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? "authenticated-admin"
                : "local-admin";

            dbContext.AuditEvents.Add(new AuditEvent(
                actor,
                "credential.caller_governance.update",
                "api_credential",
                credential.Id.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new
                {
                    credential.Name,
                    kind = credential.IsPersonal ? "personal" : "organization",
                    previous,
                    enabled = credential.EnforceCallerGovernance
                })));

            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Ok(new
            {
                credential.Id,
                credential.Name,
                kind = credential.IsPersonal ? "personal" : "organization",
                credential.EnforceCallerGovernance
            });
        });

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    public sealed record UpdateCredentialGovernanceRequest(bool Enabled);
}
