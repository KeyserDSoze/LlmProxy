using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using static LlmProxy.Api.Admin.NodeModelManagementEndpoints;

namespace LlmProxy.Api.Admin;

public static class ModelPublishingEndpoints
{
    public sealed record RenameRequest(string PublicName);
    public sealed record RuntimeStatus(Guid DeploymentId, Guid NodeId, string Status);

    public static IEndpointRouteBuilder MapModelPublishingEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled) group.RequireAuthorization("AdminRead");

        // Discovery is independent of routing: a running but disabled model must remain selectable.
        group.MapGet("/model-publications/runtime-status", async (GatewayDbContext db, IHttpClientFactory clients,
            UpstreamCredentialProtector protector, CancellationToken token) =>
        {
            var deployments = await db.Deployments.AsNoTracking().ToListAsync(token);
            var nodes = await db.Nodes.AsNoTracking().ToListAsync(token);
            var results = await Task.WhenAll(nodes.Select(async node =>
            {
                var here = deployments.Where(item => item.NodeId == node.Id).ToArray();
                if (here.Length == 0) return Array.Empty<RuntimeStatus>();
                if (string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
                    return here.Select(item => new RuntimeStatus(item.Id, node.Id, "external")).ToArray();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    var inventory = await SendAgentAsync<ManagedModelsResponse>(clients.CreateClient("node-management"), node, protector,
                        HttpMethod.Get, "/v1/models", null, timeout.Token);
                    return here.Select(item => new RuntimeStatus(item.Id, node.Id, item.ManagedInstallationId is null ? "external" :
                        inventory.Models?.FirstOrDefault(state => state.InstallationId == item.ManagedInstallationId)?.Status ?? "missing")).ToArray();
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is AgentException or HttpRequestException or OperationCanceledException)
                { return here.Select(item => new RuntimeStatus(item.Id, node.Id, "unavailable")).ToArray(); }
            }));
            return Results.Ok(results.SelectMany(items => items));
        });

        var rename = group.MapPut("/models/{id:guid}/name", async (Guid id, RenameRequest request, GatewayDbContext db,
            HttpContext context, CancellationToken token) =>
        {
            var model = await db.Models.SingleOrDefaultAsync(item => item.Id == id, token);
            if (model is null) return Results.NotFound();
            var before = model.PublicName;
            try { model.Rename(request.PublicName); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = "invalid_public_name", message = error.Message }); }
            if (await db.Models.AnyAsync(item => item.Id != id && item.PublicName == model.PublicName, token))
                return Results.Conflict(new { error = "public_name_taken", message = "Another model already uses this public name." });
            if (!await MovePoliciesAsync(db, before, model.PublicName, token))
                return Results.Conflict(new { error = "model_policy_conflict", message = "The new name already has a policy for the same owner. Resolve that policy before renaming. No changes were saved." });
            AddAudit(db, ManagedModelOperations.Caller.From(context), "model.rename", "model", id.ToString(), new { before, after = model.PublicName });
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "public_name_conflict", message = "The name or its policies conflict with an existing configuration. No changes were saved." }); }
            return Results.Ok(model);
        });
        if (entraEnabled) rename.RequireAuthorization("AdminWrite");
        return endpoints;
    }

    internal static async Task<bool> MovePoliciesAsync(GatewayDbContext db, string before, string after, CancellationToken token)
    {
        if (before == after) return true;
        var credentials = await db.RateLimitPolicies.Where(item => item.LogicalModel == before).ToListAsync(token);
        var users = await db.UserRateLimitPolicies.Where(item => item.LogicalModel == before).ToListAsync(token);
        var groups = await db.UsageGroupRateLimitPolicies.Where(item => item.LogicalModel == before).ToListAsync(token);
        // Policies may refer to a name before a model exists. In particular, the user-policy index is not unique:
        // do not silently merge scopes or reset/select a different allowance after the rename.
        var targetCredentials = await db.RateLimitPolicies.AsNoTracking().Where(item => item.LogicalModel == after).ToListAsync(token);
        var targetUsers = await db.UserRateLimitPolicies.AsNoTracking().Where(item => item.LogicalModel == after).ToListAsync(token);
        var targetGroups = await db.UsageGroupRateLimitPolicies.AsNoTracking().Where(item => item.LogicalModel == after).ToListAsync(token);
        if (credentials.Any(item => targetCredentials.Any(target => target.ApiCredentialId == item.ApiCredentialId)) ||
            users.Any(item => targetUsers.Any(target => target.OwnerTenantId == item.OwnerTenantId && target.OwnerObjectId == item.OwnerObjectId)) ||
            groups.Any(item => targetGroups.Any(target => target.UsageGroupId == item.UsageGroupId))) return false;
        // Keep policy ids (and their counters/budgets), not a fresh policy with an empty allowance.
        foreach (var policy in credentials)
            policy.Update(after, policy.RequestsPerWindow, policy.WindowSeconds, policy.Enabled);
        foreach (var policy in users)
            policy.Update(after, policy.RequestsPerWindow, policy.WindowSeconds, policy.Enabled);
        foreach (var policy in groups)
            policy.Update(after, policy.RequestsPerWindow, policy.WindowSeconds, policy.Enabled);
        foreach (var setting in await db.ContentLogSettings.Where(item => item.SummaryDefaultLogicalModel == before).ToListAsync(token))
            setting.SummaryDefaultLogicalModel = after;
        // Historical metrics, encrypted content and rollups deliberately retain the name used at request time.
        return true;
    }
}
