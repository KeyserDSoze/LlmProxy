using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class NodeHardwareMetricsCollector(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    INodeHardwareMetricsTracker tracker,
    IConfiguration configuration,
    ILogger<NodeHardwareMetricsCollector> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue("HardwareMetrics:IntervalSeconds", 10), 1, 300));
    private readonly bool _enabled = configuration.GetValue("HardwareMetrics:Enabled", true);

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
                .Where(node => node.Enabled && node.HardwareMetricsBaseAddress != null)
                .Select(node => new HardwareNode(
                    node.Id,
                    node.Name,
                    node.HardwareMetricsBaseAddress!))
                .ToListAsync(cancellationToken);

            var client = httpClientFactory.CreateClient("hardware-metrics");
            await Task.WhenAll(nodes.Select(node => CollectNodeAsync(client, node, cancellationToken)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Hardware metrics collection iteration failed.");
        }
    }

    private async Task CollectNodeAsync(HttpClient client, HardwareNode node, CancellationToken cancellationToken)
    {
        var attemptedAtUtc = DateTimeOffset.UtcNow;
        var metricsUri = InferenceEndpoint.Combine(node.HardwareMetricsBaseAddress, "/metrics");

        try
        {
            using var response = await client.GetAsync(metricsUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                tracker.RecordFailure(node.Id, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), attemptedAtUtc);
                return;
            }

            var exposition = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!DcgmPrometheusMetricsParser.TryParse(exposition, out var metrics))
            {
                tracker.RecordFailure(node.Id, "No recognized NVIDIA DCGM Prometheus metrics were exposed.", attemptedAtUtc);
                return;
            }

            tracker.RecordSuccess(
                node.Id,
                metrics.GpuCount,
                metrics.AverageGpuUtilizationPercent,
                metrics.MaxGpuUtilizationPercent,
                metrics.FramebufferUsedMiB,
                metrics.FramebufferFreeMiB,
                metrics.FramebufferUsageRatio,
                metrics.MaxTemperatureCelsius,
                metrics.TotalPowerUsageWatts,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            tracker.RecordFailure(node.Id, exception.Message, attemptedAtUtc);
            logger.LogDebug(
                exception,
                "Hardware metrics unavailable for node {NodeName} at {MetricsUri}.",
                node.Name,
                metricsUri);
        }
    }

    private sealed record HardwareNode(Guid Id, string Name, string HardwareMetricsBaseAddress);
}
