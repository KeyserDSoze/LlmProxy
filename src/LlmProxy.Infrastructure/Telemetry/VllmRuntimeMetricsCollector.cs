using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class VllmRuntimeMetricsCollector(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    INodeRuntimeMetricsTracker tracker,
    IConfiguration configuration,
    ILogger<VllmRuntimeMetricsCollector> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue("RuntimeMetrics:IntervalSeconds", 5), 1, 300));
    private readonly bool _enabled = configuration.GetValue("RuntimeMetrics:Enabled", true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        await CollectAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CollectAllAsync(stoppingToken);
        }
    }

    private async Task CollectAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var nodes = await dbContext.Nodes
                .AsNoTracking()
                .Where(node => node.Enabled)
                .Select(node => new RuntimeNode(node.Id, node.Name, node.BaseAddress))
                .ToListAsync(cancellationToken);

            var client = httpClientFactory.CreateClient("runtime-metrics");
            await Task.WhenAll(nodes.Select(node => CollectNodeAsync(client, node, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "vLLM runtime metrics collection iteration failed.");
        }
    }

    private async Task CollectNodeAsync(HttpClient client, RuntimeNode node, CancellationToken cancellationToken)
    {
        var attemptedAtUtc = DateTimeOffset.UtcNow;
        var metricsUri = InferenceEndpoint.Combine(node.BaseAddress, "/metrics");

        try
        {
            using var response = await client.GetAsync(metricsUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                tracker.RecordFailure(node.Id, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), attemptedAtUtc);
                return;
            }

            var exposition = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!VllmPrometheusMetricsParser.TryParse(exposition, out var metrics))
            {
                tracker.RecordFailure(node.Id, "No recognized vLLM Prometheus metrics were exposed.", attemptedAtUtc);
                return;
            }

            tracker.RecordSuccess(
                node.Id,
                metrics.ModelName,
                metrics.RunningRequests,
                metrics.WaitingRequests,
                metrics.KvCacheUsageRatio,
                metrics.PromptTokensTotal,
                metrics.GenerationTokensTotal,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            tracker.RecordFailure(node.Id, exception.Message, attemptedAtUtc);
            logger.LogDebug(exception, "vLLM metrics unavailable for node {NodeName} at {MetricsUri}.", node.Name, metricsUri);
        }
    }

    private sealed record RuntimeNode(Guid Id, string Name, string BaseAddress);
}
