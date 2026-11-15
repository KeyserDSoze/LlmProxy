using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using static LlmProxy.Api.Admin.NodeModelManagementEndpoints;

namespace LlmProxy.Api.Admin;

/// <summary>Restores requested routing from durable intent, not a browser's remembered start jobs.</summary>
public sealed class ManagedRoutingRecoveryWorker(IServiceScopeFactory scopes, ILogger<ManagedRoutingRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RecoverAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Managed routing reconciliation failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RecoverAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var nodes = await db.Nodes.AsNoTracking().Where(node => node.Enabled && node.Status != NodeStatus.Draining && node.ManagementBaseAddress != null).ToListAsync(token);
        foreach (var node in nodes)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                // A separate context prevents a failed node from poisoning the next one.
                using var nodeScope = scopes.CreateScope();
                await ReconcileNodeAsync(nodeScope.ServiceProvider, node.Id, timeout.Token);
            }
            catch (Exception error) when (error is AgentException or HttpRequestException or OperationCanceledException or DbUpdateConcurrencyException)
            { logger.LogDebug("Routing recovery pending for {NodeId}: {Reason}", node.Id, error.GetType().Name); }
        }
    }

    internal static bool CanRecover(LlmProxy.Domain.Deployments.ModelDeployment deployment, ManagedModelState? state, IReadOnlyList<ModelStartJob> jobs)
    {
        if (!deployment.RoutingDesired || state is null || state.DesiredRunning == false || !IsAgentRuntimeRunnable(state)) return false;
        if (deployment.ActivationStartJobId is not { } id) return true;
        var requested = jobs.FirstOrDefault(job => job.Id == id && job.InstallationId == deployment.ManagedInstallationId);
        if (requested is null) return false;
        if (requested.Status == "completed") return true;
        // After an Agent reboot or a failed attempt the guarded bootstrap may finish a replacement job.
        // Both timestamps are from the same Agent; a queued/starting operation never accepts an older success.
        return requested.Status is "interrupted" or "failed" && jobs.Any(job =>
            job.InstallationId == deployment.ManagedInstallationId && job.Status == "completed" && job.CreatedAtUtc > requested.CreatedAtUtc);
    }

    internal static async Task ReconcileNodeAsync(IServiceProvider services, Guid nodeId, CancellationToken token)
    {
        var db = services.GetRequiredService<GatewayDbContext>();
        var node = await db.Nodes.SingleOrDefaultAsync(node => node.Id == nodeId, token);
        if (node is null || !node.Enabled || node.Status == NodeStatus.Draining) return;
        var deployments = await db.Deployments.Where(item => item.NodeId == nodeId && item.ManagedInstallationId != null && item.RoutingDesired).ToListAsync(token);
        if (deployments.Count == 0) return;
        var clients = services.GetRequiredService<IHttpClientFactory>();
        var protector = services.GetRequiredService<UpstreamCredentialProtector>();
        ManagedModelsResponse? inventory = null;
        IReadOnlyList<ModelStartJob> jobs = [];
        try
        {
            using var agentTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            agentTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            inventory = await SendAgentAsync<ManagedModelsResponse>(clients.CreateClient("node-management"), node, protector, HttpMethod.Get, "/v1/models", null, agentTimeout.Token);
            if (deployments.Any(item => item.ActivationStartJobId != null))
                jobs = await SendAgentAsync<IReadOnlyList<ModelStartJob>>(clients.CreateClient("node-management"), node, protector, HttpMethod.Get, "/v1/models/start-jobs", null, agentTimeout.Token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is AgentException or HttpRequestException or OperationCanceledException)
        { /* An unobservable runtime is not ready; keep intent and suspend its routes below. */ }
        foreach (var deployment in deployments)
        {
            var state = inventory?.Models?.SingleOrDefault(item => item.InstallationId == deployment.ManagedInstallationId);
            if (CanRecover(deployment, state, jobs))
            {
                var root = OutboundRuntimeAddress(node, state!);
                using var request = new HttpRequestMessage(HttpMethod.Get, InferenceEndpoint.Combine(root!, "/health"));
                protector.ApplyBearer(request, deployment.UpstreamBearerTokenCiphertext ?? node.UpstreamBearerTokenCiphertext);
                var healthy = false;
                try
                {
                    using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    healthTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var health = await clients.CreateClient("probe").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, healthTimeout.Token);
                    healthy = health.IsSuccessStatusCode;
                }
                catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or OperationCanceledException)
                { /* A transport failure must not leave this deployment enabled. */ }
                if (healthy)
                {
                    if (!deployment.Enabled) node.RecordHealthSuccess(DateTimeOffset.UtcNow, 0, 1);
                    deployment.ConfigureRuntime(root, deployment.CatalogModelId, deployment.ManagedInstallationId);
                    deployment.Enable();
                }
                else deployment.SuspendRouting();
            }
            else deployment.SuspendRouting();
        }
        // Intent fields are concurrency tokens: a concurrent operator Stop wins.
        var currentNode = await db.Nodes.AsNoTracking().SingleAsync(item => item.Id == nodeId, token);
        if (!currentNode.Enabled || currentNode.Status == NodeStatus.Draining) return;
        await db.SaveChangesAsync(token);
    }
}
