using System.Diagnostics;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Health;

public sealed class NodeHealthMonitor(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    UpstreamCredentialProtector upstreamCredentialProtector,
    IConfiguration configuration,
    ILogger<NodeHealthMonitor> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Health:IntervalSeconds", 10), 1, 300));
    private readonly int _healthyAfterSuccesses = Math.Clamp(configuration.GetValue("Health:HealthyAfterSuccesses", 2), 1, 20);
    private readonly int _unhealthyAfterFailures = Math.Clamp(configuration.GetValue("Health:UnhealthyAfterFailures", 3), 1, 20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CheckAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
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
            var activeManagedRuntimes = await dbContext.Deployments.AsNoTracking()
                .Where(deployment => deployment.Enabled && deployment.ManagedInstallationId != null
                    && deployment.RuntimeBaseAddress != null)
                .Select(deployment => new {
                    deployment.NodeId,
                    RuntimeBaseAddress = deployment.RuntimeBaseAddress!,
                    deployment.UpstreamBearerTokenCiphertext
                })
                .ToListAsync(cancellationToken);
            var serviceRoots = activeManagedRuntimes.GroupBy(x => x.NodeId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var client = httpClientFactory.CreateClient("health");

            foreach (var node in nodes.Where(node => node.Status != NodeStatus.Draining))
            {
                // A physical node stays routable when ANY of its enabled managed runtimes is healthy.
                // A stopped second model must not make a still-running first model disappear.
                HealthCheckResult result;
                if (serviceRoots.TryGetValue(node.Id, out var runtimes))
                {
                    result = new HealthCheckResult(false, 0, "No managed runtime is healthy.");
                    foreach (var runtime in runtimes)
                    {
                        var check = await CheckNodeAsync(client, node, runtime.RuntimeBaseAddress,
                            runtime.UpstreamBearerTokenCiphertext ?? node.UpstreamBearerTokenCiphertext,
                            cancellationToken);
                        if (check.Success) { result = check; break; }
                        result = check;
                    }
                }
                else result = await CheckNodeAsync(client, node, node.BaseAddress,
                    node.UpstreamBearerTokenCiphertext, cancellationToken);
                var checkedAtUtc = DateTimeOffset.UtcNow;

                if (result.Success)
                {
                    node.RecordHealthSuccess(checkedAtUtc, result.LatencyMilliseconds, _healthyAfterSuccesses);
                }
                else
                {
                    node.RecordHealthFailure(
                        checkedAtUtc,
                        result.LatencyMilliseconds,
                        result.Error,
                        _unhealthyAfterFailures);
                }
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

    private async Task<HealthCheckResult> CheckNodeAsync(
        HttpClient client,
        InferenceNode node,
        string runtimeRoot,
        string? upstreamBearerTokenCiphertext,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var healthUri = InferenceEndpoint.Combine(runtimeRoot, "/health");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            upstreamCredentialProtector.ApplyBearer(request, upstreamBearerTokenCiphertext);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new HealthCheckResult(true, stopwatch.ElapsedMilliseconds, null);
            }

            var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
            logger.LogWarning(
                "Health check returned {StatusCode} for node {NodeName} at {HealthUri}.",
                (int)response.StatusCode,
                node.Name,
                healthUri);
            return new HealthCheckResult(false, stopwatch.ElapsedMilliseconds, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException exception)
        {
            logger.LogWarning(exception, "Health check timed out for node {NodeName} at {HealthUri}.", node.Name, healthUri);
            return new HealthCheckResult(false, stopwatch.ElapsedMilliseconds, "Health check timed out.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Health check failed for node {NodeName} at {HealthUri}.", node.Name, healthUri);
            return new HealthCheckResult(false, stopwatch.ElapsedMilliseconds, exception.Message);
        }
    }

    private sealed record HealthCheckResult(bool Success, long LatencyMilliseconds, string? Error);
}
