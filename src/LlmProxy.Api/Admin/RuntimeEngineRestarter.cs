using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Makes running models use a freshly updated engine: one model at a time it takes the model out of routing, lets the requests
/// in flight finish, stops it, starts it again (the Agent waits for its /health) and puts it back. A model that does not come
/// back keeps its routing off and the rest are left alone, so a broken engine never takes the whole server down.
/// </summary>
internal static class RuntimeEngineRestarter
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(40);
    /// <summary>How often the Agent is asked how a start is going (shortened by tests).</summary>
    internal static TimeSpan Poll { get; set; } = TimeSpan.FromSeconds(5);

    public static async Task<AdminOperationOutcome> RestartRunningModelsAsync(
        IServiceProvider services, Guid nodeId, string engineId, string engineLabel, Action<string> progress, CancellationToken token)
    {
        var db = services.GetRequiredService<GatewayDbContext>();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("node-management");
        var protector = services.GetRequiredService<UpstreamCredentialProtector>();
        var load = services.GetRequiredService<IRequestLoadTracker>();
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == nodeId, token);
        if (node is null || string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
            return new AdminOperationOutcome(false, "The server has no management Agent.");

        var targets = await RunningModelsAsync(client, node, protector, engineId, token);
        if (targets.Count == 0) return new AdminOperationOutcome(true, $"No running model uses {engineLabel}, so nothing needed a restart.");

        var done = 0;
        foreach (var target in targets)
        {
            var step = $"{done + 1} of {targets.Count}";
            var name = target.ProviderModelName ?? target.InstallationId;
            var deployment = await db.Deployments.SingleOrDefaultAsync(item => item.NodeId == nodeId && item.ManagedInstallationId == target.InstallationId, token);
            var wasRouted = deployment?.Enabled == true;
            try
            {
                if (wasRouted)
                {
                    progress($"Restarting {name} ({step}): taking it out of routing");
                    deployment!.Disable();
                    await db.SaveChangesAsync(token);
                    await WaitForDrainAsync(load, deployment.Id, name, step, progress, token);
                }

                progress($"Restarting {name} ({step}): stopping");
                var installation = Uri.EscapeDataString(target.InstallationId);
                await NodeModelManagementEndpoints.SendAgentAsync<JsonElement>(client, node, protector, HttpMethod.Post, $"/v1/models/{installation}/stop", new { action = "stop" }, token);

                progress($"Restarting {name} ({step}): starting on the new {engineLabel}");
                var job = await NodeModelManagementEndpoints.SendAgentAsync<NodeModelManagementEndpoints.ModelStartJob>(
                    client, node, protector, HttpMethod.Post, $"/v1/models/{installation}/start-jobs", null, token);
                await WaitForStartAsync(client, node, protector, job.Id, name, step, progress, token);

                if (wasRouted)
                {
                    deployment!.Enable();
                    await db.SaveChangesAsync(token);
                }
                done++;
            }
            catch (NodeModelManagementEndpoints.AgentException error)
            {
                var routing = wasRouted ? " Routing for it stays off until you start it." : "";
                return new AdminOperationOutcome(false, $"{name} did not come back after the {engineLabel} update: {error.Message}.{routing}" +
                    (done > 0 ? $" {done} model{(done == 1 ? "" : "s")} before it restarted fine." : "") + " The other models were left running.");
            }
        }
        return new AdminOperationOutcome(true, $"{done} model{(done == 1 ? "" : "s")} restarted on the new {engineLabel}.");
    }

    internal static async Task<IReadOnlyList<NodeModelManagementEndpoints.ManagedModelState>> RunningModelsAsync(
        HttpClient client, LlmProxy.Domain.Nodes.InferenceNode node, UpstreamCredentialProtector protector, string engineId, CancellationToken token)
    {
        var inventory = await NodeModelManagementEndpoints.SendAgentAsync<NodeModelManagementEndpoints.ManagedModelsResponse>(
            client, node, protector, HttpMethod.Get, "/v1/models", null, token);
        return (inventory.Models ?? []).Where(model => model.Runtime == engineId && model.Status == "running")
            .OrderBy(model => model.Port).ToArray();
    }

    private static async Task WaitForDrainAsync(IRequestLoadTracker load, Guid deploymentId, string name, string step, Action<string> progress, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + DrainTimeout;
        while (load.GetActive(deploymentId) is var active and > 0 && DateTimeOffset.UtcNow < deadline)
        {
            progress($"Restarting {name} ({step}): waiting for {active} request{(active == 1 ? "" : "s")} to finish");
            await Task.Delay(Poll < TimeSpan.FromSeconds(2) ? Poll : TimeSpan.FromSeconds(2), token);
        }
    }

    private static async Task WaitForStartAsync(HttpClient client, LlmProxy.Domain.Nodes.InferenceNode node, UpstreamCredentialProtector protector,
        Guid jobId, string name, string step, Action<string> progress, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(Poll, token);
            var jobs = await NodeModelManagementEndpoints.SendAgentAsync<IReadOnlyList<NodeModelManagementEndpoints.ModelStartJob>>(
                client, node, protector, HttpMethod.Get, "/v1/models/start-jobs", null, token);
            var current = jobs.FirstOrDefault(job => job.Id == jobId);
            if (current is null) throw new NodeModelManagementEndpoints.AgentException("The Agent forgot the start job");
            if (current.Status == "completed") return;
            if (current.Status is "failed" or "cancelled" or "interrupted")
                throw new NodeModelManagementEndpoints.AgentException(current.Error?.Split('\n')[0] ?? current.Detail);
            progress($"Restarting {name} ({step}): {current.Detail}");
        }
        throw new NodeModelManagementEndpoints.AgentException($"it did not become healthy within {StartTimeout.TotalMinutes:0} minutes");
    }
}
