using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Health;

public sealed class NodeHealthMonitor(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<NodeHealthMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        await CheckAllAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckAllAsync(stoppingToken);
        }
    }

    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var nodes = await dbContext.Nodes.Where(node => node.Enabled).ToListAsync(cancellationToken);
            var client = httpClientFactory.CreateClient("health");

            foreach (var node in nodes.Where(node => node.Status != NodeStatus.Draining))
            {
                var status = await CheckNodeAsync(client, node, cancellationToken);
                node.SetHealth(status, DateTimeOffset.UtcNow);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Node health monitor iteration failed.");
        }
    }

    private async Task<NodeStatus> CheckNodeAsync(HttpClient client, InferenceNode node, CancellationToken cancellationToken)
    {
        try
        {
            var healthUri = new Uri($"{node.BaseAddress.TrimEnd('/')}/health");
            using var response = await client.GetAsync(healthUri, cancellationToken);
            return response.IsSuccessStatusCode ? NodeStatus.Healthy : NodeStatus.Unhealthy;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Health check failed for node {NodeName} at {BaseAddress}.", node.Name, node.BaseAddress);
            return NodeStatus.Unhealthy;
        }
    }
}
