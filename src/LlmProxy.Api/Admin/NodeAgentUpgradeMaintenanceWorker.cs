using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Drain-and-resume coordinator for Agent binary upgrades. The Agent is only a
/// management/tunnel process: all Docker inference containers remain running.
/// This durable SQL state machine never automatically starts a GPU model.
/// The current node Draining state and Redis admission block prevent new work
/// until an authenticated Agent heartbeat and managed-runtime health checks.
/// </summary>
public sealed class NodeAgentUpgradeMaintenanceWorker(
    IServiceScopeFactory scopes,
    ILogger<NodeAgentUpgradeMaintenanceWorker> logger) : BackgroundService
{
    public static bool IsVerifiedUpgrade(string? installed, string? status, string target) =>
        installed == target && status is not null &&
        status.StartsWith("succeeded:" + target, StringComparison.Ordinal);

    public static bool IsFailedUpgrade(string? status, string target) =>
        status == "failed:" + target ||
        status?.StartsWith("failed:" + target + ":", StringComparison.Ordinal) == true;

    // Downloads still progress with a healthy old Agent heartbeat. Only flag
    // unattended upgrades after both an extended elapsed time and sustained
    // loss of contact. This is an alert, not permission to restart a GPU model.
    public static bool IsUpgradeContactStalled(
        DateTimeOffset? startedAt, DateTimeOffset? lastHeartbeat, DateTimeOffset now) =>
        startedAt.HasValue && startedAt <= now.AddMinutes(-12) &&
        (!lastHeartbeat.HasValue || lastHeartbeat < now.AddMinutes(-3));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                var nodeIds = await db.NodeEnrollments.AsNoTracking()
                    .Where(a => a.NodeId != null && a.AgentUpgradeTargetVersion != null)
                    .Select(a => a.NodeId!.Value)
                    .ToArrayAsync(stoppingToken);
                foreach (var id in nodeIds)
                {
                    try { await ProcessNodeAsync(id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception,
                            "Agent upgrade maintenance for node {NodeId} will be retried without enabling inference routing.", id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not inspect pending Agent upgrade maintenance.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessNodeAsync(Guid id, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var coordinator = scope.ServiceProvider.GetRequiredService<INodeMaintenanceCoordinator>();

        // Never hold a PostgreSQL row lock while sending Agent HTTP probes.
        // An 18-second health check could otherwise block the authenticated
        // heartbeat update and recreate the Agent's 15-second timeout issue.
        // The phase/version is rechecked after reacquiring the short SQL lock.
        bool? preflightHealthy = null;
        var preflightError = "";
        var snapshot = await db.NodeEnrollments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.NodeId == id, token);
        var expectedTarget = snapshot?.AgentUpgradeTargetVersion;
        if (expectedTarget is not null &&
            snapshot?.AgentUpgradeMaintenanceStage is "updating" or "verifying" or "needs_attention" &&
            (IsVerifiedUpgrade(snapshot.AgentVersion, snapshot.AgentUpdateStatus, expectedTarget) ||
             IsFailedUpgrade(snapshot.AgentUpdateStatus, expectedTarget)) &&
            snapshot.LastHeartbeatAtUtc >= DateTimeOffset.UtcNow.AddSeconds(-40))
        {
            var candidate = await db.Nodes.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == id, token);
            if (candidate?.Status == NodeStatus.Draining)
            {
                var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                var protector = scope.ServiceProvider.GetRequiredService<UpstreamCredentialProtector>();
                (preflightHealthy, preflightError) = await ProbeAsync(
                    db, candidate, factory, protector, token);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(token);

        // Multiple Gateway replicas share this upgrade. Never let two workers
        // resume a node or queue a version in parallel.
        if (!await TryLockNodeAsync(db, id, token)) return;

        var enrollment = await db.NodeEnrollments.SingleOrDefaultAsync(x => x.NodeId == id, token);
        if (enrollment?.AgentUpgradeTargetVersion is not string target) return;
        var node = await db.Nodes.SingleOrDefaultAsync(x => x.Id == id, token);
        if (node is null)
        {
            enrollment.AgentUpgradeTargetVersion = null;
            enrollment.AgentUpgradeMaintenanceStage = null;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }
        if (enrollment.AgentUpgradeMaintenanceStage == "releasing")
        {
            // Health already passed and node is Healthy. Retry an interrupted
            // Redis release instead of repeating GPU/runtime probes.
            if (await coordinator.TryResumeAsync(id, token))
            {
                enrollment.AgentUpgradeMaintenanceStage = null;
                enrollment.AgentUpgradeTargetVersion = null;
                enrollment.AgentUpgradeStartedAtUtc = null;
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return;
        }

        if (node.Status != NodeStatus.Draining)
        {
            // An operator might deliberately disable the node mid-upgrade.
            // Never force it online. Preserve the maintenance marker until
            // an administrator investigates.
            enrollment.AgentUpgradeMaintenanceStage = "needs_attention";
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return;
        }

        if (enrollment.AgentUpgradeMaintenanceStage == "updating" &&
            IsUpgradeContactStalled(enrollment.AgentUpgradeStartedAtUtc,
                enrollment.LastHeartbeatAtUtc, DateTimeOffset.UtcNow))
        {
            enrollment.AgentUpgradeMaintenanceStage = "needs_attention";
            db.AuditEvents.Add(new AuditEvent("llmproxy-agent-upgrade",
                "agent.update.offline_timeout", "node", id.ToString(), null,
                JsonSerializer.Serialize(new { targetVersion = target,
                    note = "No heartbeat after extended upgrade period; routing remains safely drained." })));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            logger.LogWarning(
                "Node {NodeId} Agent has not sent a heartbeat for over three minutes during a 12+ minute upgrade. "+
                "The node remains drained. Check systemd update unit and Agent logs on the host.",
                id);
            return;
        }

        if (enrollment.AgentUpgradeMaintenanceStage == "draining")
        {
            if (!await coordinator.TryConfirmDrainAsync(id, token)) return;
            var status = await coordinator.GetStatusAsync(id, token);
            if (!status.Drained) return;

            enrollment.DesiredAgentVersion = target;
            enrollment.AgentUpgradeMaintenanceStage = "updating";
            db.AuditEvents.Add(new AuditEvent("llmproxy-agent-upgrade",
                "agent.update.after_drain", "node", id.ToString(), null,
                JsonSerializer.Serialize(new { version = target, status.ActiveRequests })));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            logger.LogInformation("Node {NodeId} fully drained; requested Agent version {Version}. No Docker models were stopped.",
                id, target);
            return;
        }

        var upgraded = IsVerifiedUpgrade(enrollment.AgentVersion, enrollment.AgentUpdateStatus, target);
        var rolledBack = IsFailedUpgrade(enrollment.AgentUpdateStatus, target);
        if (!upgraded && !rolledBack) return;
        if (enrollment.LastHeartbeatAtUtc < DateTimeOffset.UtcNow.AddSeconds(-40)) return;
        if (enrollment.AgentUpgradeMaintenanceStage is not ("updating" or "verifying" or "needs_attention"))
            return;
        if (!await coordinator.TryConfirmDrainAsync(id, token)) return;
        var before = await coordinator.GetStatusAsync(id, token);
        if (!before.Drained) return;

        if (preflightHealthy is null || expectedTarget != target)
            return; // A heartbeat/upgrade state transition raced with the probe.
        if (preflightHealthy != true)
        {
            // Fail closed. Keep the admission gate and Draining node while an
            // operator can inspect the runtime; never run docker start here.
            var firstFailure = enrollment.AgentUpgradeMaintenanceStage != "needs_attention";
            enrollment.AgentUpgradeMaintenanceStage = "needs_attention";
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            if (firstFailure)
                logger.LogWarning("Node {NodeId} Agent changed but routing stays drained: {Error}",
                    id, preflightError);
            return;
        }

        // Model containers were never stopped. All originally enabled
        // deployments are still enabled, unless an operator changed them.
        node.Disable();
        node.Enable();
        node.RecordHealthSuccess(DateTimeOffset.UtcNow, 0, healthyAfterSuccesses: 1);
        enrollment.AgentUpgradeMaintenanceStage = "releasing";
        db.AuditEvents.Add(new AuditEvent("llmproxy-agent-upgrade",
            rolledBack ? "agent.update.rollback.routing_recovered" : "agent.update.routing_recovered",
            "node", id.ToString(), null,
            JsonSerializer.Serialize(new { version = target, verifiedAgentVersion = enrollment.AgentVersion })));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        // The durable "releasing" state repairs a Redis failure on the next tick.
        if (await coordinator.TryResumeAsync(id, token))
            logger.LogInformation("Restored verified inference routing on node {NodeId} after Agent upgrade attempt.", id);
    }

    private static async Task<bool> TryLockNodeAsync(GatewayDbContext db, Guid id, CancellationToken token)
    {
        var conn = db.Database.GetDbConnection();
        await using var command = conn.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT pg_try_advisory_xact_lock(hashtext('llmproxy_agent_upgrade'), hashtext(@node_id))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "node_id";
        parameter.Value = id.ToString("N");
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(token);
        return value is true;
    }

    private static async Task<(bool Ok, string Error)> ProbeAsync(
        GatewayDbContext db, InferenceNode node,
        IHttpClientFactory? factory, UpstreamCredentialProtector? protector, CancellationToken token)
    {
        if (factory is null || protector is null)
            return (false, "Missing Agent health-probe dependencies.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            var client = factory.CreateClient("node-management");
            var endpoint = InferenceEndpoint.Combine(node.ManagementBaseAddress!, "/health");
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                protector.ApplyBearer(request, node.ManagementBearerTokenCiphertext);
                using var response = await client.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                    return (false, "Agent /health returned HTTP " + (int)response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                if (!document.RootElement.TryGetProperty("service", out var service) ||
                    service.GetString() != "llmproxy-node-agent")
                    return (false, "Agent health response came from an unexpected service.");
            }

            JsonDocument inventory;
            using (var request = new HttpRequestMessage(HttpMethod.Get,
                InferenceEndpoint.Combine(node.ManagementBaseAddress!, "/v1/models")))
            {
                protector.ApplyBearer(request, node.ManagementBearerTokenCiphertext);
                using var response = await client.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                    return (false, "Agent model inventory returned HTTP " + (int)response.StatusCode);
                inventory = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            }
            using (inventory)
            {
                var models = inventory.RootElement.GetProperty("models");
                var managed = await db.Deployments.AsNoTracking().Where(d =>
                    d.NodeId == node.Id && d.Enabled && d.ManagedInstallationId != null)
                    .Select(d => new { d.ManagedInstallationId, d.RuntimeBaseAddress,
                        d.UpstreamBearerTokenCiphertext }).ToArrayAsync(cts.Token);
                var checker = factory.CreateClient("probe");
                foreach (var deployment in managed)
                {
                    var runtime = models.EnumerateArray().FirstOrDefault(item =>
                        item.TryGetProperty("installationId", out var inst) &&
                        inst.GetString() == deployment.ManagedInstallationId);
                    if (runtime.ValueKind != JsonValueKind.Object ||
                        !runtime.TryGetProperty("status", out var status) ||
                        status.GetString() != "running")
                        return (false, "An enabled managed deployment is no longer running on the Agent.");
                    var runtimeRoot = deployment.RuntimeBaseAddress;
                    if (string.IsNullOrWhiteSpace(runtimeRoot) &&
                        runtime.TryGetProperty("runtimeBaseAddress", out var address))
                        runtimeRoot = address.GetString();
                    if (string.IsNullOrWhiteSpace(runtimeRoot))
                        return (false, "A running deployment has no registered serving address.");
                    using var probe = new HttpRequestMessage(HttpMethod.Get,
                        InferenceEndpoint.Combine(runtimeRoot, "/health"));
                    protector.ApplyBearer(probe, deployment.UpstreamBearerTokenCiphertext ??
                        node.UpstreamBearerTokenCiphertext);
                    using var response = await checker.SendAsync(probe, cts.Token);
                    if (!response.IsSuccessStatusCode)
                        return (false, "A managed runtime /health probe returned HTTP " + (int)response.StatusCode);
                }
            }
            return (true, "");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return (false, "Agent/runtime health verification timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
            or InvalidOperationException or UriFormatException)
        { return (false, ex.GetType().Name + " during Agent/runtime readiness verification."); }
    }
}
