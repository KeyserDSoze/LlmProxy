using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Telemetry;

/// <summary>Per-managed-deployment metrics, isolated from legacy node-scoped vLLM routing signals.</summary>
public sealed class DeploymentRuntimeMetricsCollector(
    IServiceScopeFactory scopeFactory, IHttpClientFactory clientFactory,
    IDeploymentRuntimeMetricsTracker tracker, UpstreamCredentialProtector protector,
    IConfiguration configuration, ILogger<DeploymentRuntimeMetricsCollector> logger) : BackgroundService
{
    private readonly bool _enabled = configuration.GetValue("RuntimeMetrics:Enabled", true);
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(Math.Clamp(
        configuration.GetValue("RuntimeMetrics:IntervalSeconds", 5), 1, 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        await CollectAsync(stoppingToken);
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await CollectAsync(stoppingToken);
    }

    private async Task CollectAsync(CancellationToken token)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var targets = await (from deployment in db.Deployments.AsNoTracking()
                join node in db.Nodes.AsNoTracking() on deployment.NodeId equals node.Id
                where deployment.ManagedInstallationId != null && deployment.Enabled &&
                    deployment.RuntimeBaseAddress != null && node.Enabled
                select new RuntimeTarget(deployment.Id, deployment.NodeId, deployment.RuntimeBaseAddress!,
                    deployment.UpstreamBearerTokenCiphertext ?? node.UpstreamBearerTokenCiphertext))
                .ToListAsync(token);
            tracker.RetainOnly(targets.Select(x => x.DeploymentId).ToHashSet());
            var client = clientFactory.CreateClient("runtime-metrics");
            await Task.WhenAll(targets.Select(target => CollectTargetAsync(client, target, token)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {}
        catch (Exception error) { logger.LogWarning(error, "Managed deployment metrics cycle failed."); }
    }

    private async Task CollectTargetAsync(HttpClient client, RuntimeTarget target, CancellationToken token)
    {
        var attempted = DateTimeOffset.UtcNow;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                InferenceEndpoint.Combine(target.BaseAddress, "/metrics"));
            protector.ApplyBearer(request, target.BearerCiphertext);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                tracker.RecordFailure(target.DeploymentId, target.NodeId,
                    $"HTTP {(int)response.StatusCode}", attempted);
                return;
            }

            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, token);
            var exposition = await response.Content.ReadAsStringAsync(token);
            if (!MultiRuntimePrometheusMetricsParser.TryParse(exposition, out var parsed))
            {
                tracker.RecordFailure(target.DeploymentId, target.NodeId,
                    "No supported runtime metrics exposed.", attempted);
                return;
            }

            tracker.RecordSuccess(target.DeploymentId, target.NodeId, parsed.Runtime,
                parsed.ModelName, parsed.Running, parsed.Waiting, parsed.CacheUsage,
                parsed.PromptTokens, parsed.GenerationTokens, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {}
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            tracker.RecordFailure(target.DeploymentId, target.NodeId,
                error.GetType().Name, attempted);
        }
    }

    private sealed record RuntimeTarget(Guid DeploymentId, Guid NodeId, string BaseAddress, string? BearerCiphertext);
}
