using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using static LlmProxy.Api.Admin.NodeModelManagementEndpoints;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Stop, force stop and uninstall of an Agent-managed model. The same code serves the synchronous endpoints and the
/// background jobs. Routing is switched off first, so a failure never leaves a half-removed model receiving traffic,
/// and every outcome (including a failure with the Agent's own message) is written to the audit log.
/// </summary>
internal static class ManagedModelOperations
{
    /// <summary>Who asked; captured before the work leaves the HTTP request.</summary>
    public sealed record Caller(string Actor, string? SourceIp)
    {
        public static Caller From(HttpContext context) => new(
            context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirst("preferred_username")?.Value
                  ?? context.User.FindFirst(ClaimTypes.Email)?.Value
                  ?? context.User.Identity?.Name
                  ?? "authenticated-admin"
                : "local-admin",
            context.Connection.RemoteIpAddress?.ToString());
    }

    public enum Kind { Stop, ForceStop, Remove }

    public static string Slug(Kind kind) => kind switch { Kind.Stop => "stop", Kind.ForceStop => "force-stop", _ => "remove" };

    public static string Verb(Kind kind) => kind switch { Kind.Stop => "Stop", Kind.ForceStop => "Force stop", _ => "Uninstall" };

    public static async Task<AdminOperationOutcome> RunAsync(
        IServiceProvider services, Kind kind, Guid deploymentId, Caller caller, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<GatewayDbContext>();
        var deployment = await db.Deployments.FindAsync([deploymentId], cancellationToken);
        if (deployment is null) return new(false, "The deployment no longer exists.");
        if (string.IsNullOrWhiteSpace(deployment.ManagedInstallationId))
            return new(false, "This deployment is not managed by a server Agent.");
        var node = await db.Nodes.FindAsync([deployment.NodeId], cancellationToken);
        if (node is null || string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
            return new(false, "No management Agent is configured for this server.");

        var action = "model." + Slug(kind).Replace('-', '_');
        var details = new { node.Name, deployment.ManagedInstallationId, deployment.CatalogModelId };

        // Take it out of routing before asking the remote runtime to do anything.
        deployment.Disable();
        if (kind != Kind.Remove) AddAudit(db, caller, action, "deployment", deploymentId.ToString(), details);
        await db.SaveChangesAsync(cancellationToken);

        var installation = Uri.EscapeDataString(deployment.ManagedInstallationId);
        try
        {
            var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("node-management");
            var protector = services.GetRequiredService<UpstreamCredentialProtector>();
            string message;
            switch (kind)
            {
                case Kind.Stop:
                    await SendAgentAsync<ManagedModelState>(client, node, protector, HttpMethod.Post, $"/v1/models/{installation}/stop", new { action = "stop" }, cancellationToken);
                    message = "Stopped and verified. Routing stays off until you start it again.";
                    break;
                case Kind.ForceStop:
                    await SendAgentAsync<ManagedModelState>(client, node, protector, HttpMethod.Post, $"/v1/models/{installation}/force-stop", new { action = "force-stop" }, cancellationToken);
                    message = "Force-stopped and verified. Routing stays off until you start it again.";
                    break;
                default:
                    var alreadyGone = false;
                    try { await SendAgentAsync<JsonElement>(client, node, protector, HttpMethod.Delete, $"/v1/models/{installation}", null, cancellationToken); }
                    catch (AgentException missing) when (missing.StatusCode == StatusCodes.Status404NotFound) { alreadyGone = true; }
                    AddAudit(db, caller, action, "deployment", deploymentId.ToString(), details);
                    db.Deployments.Remove(deployment);
                    await db.SaveChangesAsync(cancellationToken);
                    message = alreadyGone
                        ? "The Agent no longer had this installation; the stale deployment was removed."
                        : "Uninstalled. Cached model files are kept for reuse.";
                    break;
            }
            return new(true, message);
        }
        catch (AgentException error)
        {
            await RecordFailureAsync(db, caller, action, deploymentId, details, error.Message);
            return new(false, $"Routing was switched off, but the Agent could not complete the {Verb(kind).ToLowerInvariant()}: {error.Message}");
        }
    }

    private static async Task RecordFailureAsync(GatewayDbContext db, Caller caller, string action, Guid deploymentId, object details, string error)
    {
        try
        {
            AddAudit(db, caller, action + ".failed", "deployment", deploymentId.ToString(), new { details, error = error.Length > 600 ? error[..600] : error });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException) { /* The operation result still reaches the operator. */ }
    }
}
