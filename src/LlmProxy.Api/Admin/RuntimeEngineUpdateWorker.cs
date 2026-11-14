using System.Text.Json;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Updates serving engines at the time the administrator chose. For each due policy it asks the Agent to pull (or rebuild)
/// the image, waits for the result and records it. A running model keeps the image it started from, so an update never
/// interrupts inference; models pick the new engine the next time they start. Replicas never act twice: a policy is
/// claimed with a single conditional UPDATE before anything is sent.
/// </summary>
public sealed class RuntimeEngineUpdateWorker(IServiceScopeFactory scopes, ILogger<RuntimeEngineUpdateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxJobTime = TimeSpan.FromMinutes(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Engine auto-update pass failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunDueAsync(CancellationToken token)
    {
        List<(Guid NodeId, string EngineId)> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var now = DateTimeOffset.UtcNow;
            due = (await db.RuntimeEnginePolicies.AsNoTracking().Where(policy => policy.AutoUpdate).ToListAsync(token))
                .Where(policy => RuntimeEngineSchedule.IsDue(policy, now))
                .Select(policy => (policy.NodeId, policy.EngineId)).ToList();
        }

        // One node at a time: an Agent runs one engine operation at a time.
        foreach (var group in due.GroupBy(item => item.NodeId))
            foreach (var (nodeId, engineId) in group)
            {
                if (token.IsCancellationRequested) return;
                if (await ClaimAsync(nodeId, engineId, token)) await UpdateOneAsync(nodeId, engineId, token);
            }
    }

    /// <summary>True for exactly one replica per attempt window.</summary>
    private async Task<bool> ClaimAsync(Guid nodeId, string engineId, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var now = DateTimeOffset.UtcNow;
        var retryBefore = now - RuntimeEngineSchedule.RetryInterval;
        var claimed = await db.RuntimeEnginePolicies
            .Where(policy => policy.NodeId == nodeId && policy.EngineId == engineId && policy.AutoUpdate &&
                (policy.LastAttemptAtUtc == null || policy.LastAttemptAtUtc <= retryBefore))
            .ExecuteUpdateAsync(set => set.SetProperty(policy => policy.LastAttemptAtUtc, now), token);
        return claimed == 1;
    }

    private async Task UpdateOneAsync(Guid nodeId, string engineId, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var protector = scope.ServiceProvider.GetRequiredService<UpstreamCredentialProtector>();
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == nodeId, token);
        string result, message;
        var reachedAgent = true;
        try
        {
            if (node is null || string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
                (result, message) = ("skipped", "The server has no management Agent.");
            else
            {
                (result, message) = await RunOnAgentAsync(node, engineId, factory.CreateClient("node-management"), protector, token);
                // A new image only reaches models that start after it; bring the running ones onto it, one at a time.
                if (result == "updated" && (await db.RuntimeEnginePolicies.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.NodeId == nodeId && item.EngineId == engineId, token))?.RestartModels == true)
                {
                    var label = engineId;
                    var outcome = await RuntimeEngineRestarter.RestartRunningModelsAsync(scope.ServiceProvider, nodeId, engineId, label, _ => { }, token);
                    message += " " + outcome.Message;
                    if (!outcome.Succeeded) result = "failed";
                }
            }
        }
        catch (NodeModelManagementEndpoints.AgentException error)
        {
            // Busy or unreachable: do not count the slot as done, it is retried after the retry interval.
            reachedAgent = error.StatusCode is not (null or StatusCodes.Status409Conflict);
            (result, message) = error.StatusCode == StatusCodes.Status409Conflict
                ? ("waiting", "The Agent was busy with another engine operation; trying again shortly.")
                : ("waiting", "The Agent could not be reached: " + error.Message);
            if (error.StatusCode is StatusCodes.Status404NotFound or StatusCodes.Status400BadRequest) { reachedAgent = true; result = "failed"; }
        }

        var now = DateTimeOffset.UtcNow;
        var row = await db.RuntimeEnginePolicies.SingleOrDefaultAsync(item => item.NodeId == nodeId && item.EngineId == engineId, token);
        if (row is null) return;
        row.LastResult = result;
        row.LastMessage = message.Length > 600 ? message[..600] : message;
        if (result != "waiting" && reachedAgent) row.LastRunAtUtc = now;
        if (result is "updated" or "failed")
            db.AuditEvents.Add(new LlmProxy.Domain.Audit.AuditEvent("scheduler", "engine.auto-update", "node", nodeId.ToString(), null,
                JsonSerializer.Serialize(new { engine = engineId, node = node?.Name, result, message = row.LastMessage })));
        await db.SaveChangesAsync(token);
        logger.LogInformation("Engine auto-update {Engine} on {Node}: {Result}", engineId, node?.Name, result);
    }

    private static async Task<(string Result, string Message)> RunOnAgentAsync(InferenceNode node, string engineId, HttpClient client,
        UpstreamCredentialProtector protector, CancellationToken token)
    {
        var engines = await NodeModelManagementEndpoints.SendAgentAsync<IReadOnlyList<AgentEngine>>(
            client, node, protector, HttpMethod.Get, "/v1/runtime-engines", null, token);
        var engine = engines.FirstOrDefault(item => item.Id == engineId);
        if (engine is null) return ("skipped", "This Agent does not know the engine.");
        // Automatic updates keep installed engines current; they never install one the administrator has not chosen.
        if (!engine.Installed) return ("skipped", "Not installed.");

        var job = await NodeModelManagementEndpoints.SendAgentAsync<AgentEngineJob>(client, node, protector, HttpMethod.Post,
            $"/v1/runtime-engines/{Uri.EscapeDataString(engineId)}/jobs", new { action = "update" }, token);

        var deadline = DateTimeOffset.UtcNow + MaxJobTime;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(Poll, token);
            var jobs = await NodeModelManagementEndpoints.SendAgentAsync<IReadOnlyList<AgentEngineJob>>(
                client, node, protector, HttpMethod.Get, "/v1/runtime-engines/jobs", null, token);
            var current = jobs.FirstOrDefault(item => item.Id == job.Id);
            if (current is null) return ("failed", "The Agent forgot the update job.");
            switch (current.Status)
            {
                case "completed":
                    var changed = current.Result is { ValueKind: JsonValueKind.Object } result && result.TryGetProperty("changed", out var flag) && flag.ValueKind == JsonValueKind.True;
                    return (changed ? "updated" : "up_to_date", current.Detail);
                case "failed" or "cancelled" or "interrupted":
                    return ("failed", current.Error ?? current.Detail);
            }
        }
        return ("failed", "The update did not finish within 90 minutes; it may still be running on the Agent.");
    }
}
