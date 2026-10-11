using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Reconciles durable Node Agent jobs without relying on an open Admin tab.
/// Only the Agent owns installation execution; this worker registers completed,
/// still-present installations in the Gateway and never automatically starts one.
/// </summary>
public sealed class ManagedInstallFinalizationWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory clients,
    UpstreamCredentialProtector protector,
    ILogger<ManagedInstallFinalizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await NodeModelManagementEndpoints.ReconcileCompletedJobsAsync(
                    scopeFactory, clients, protector, logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // One unavailable node must not terminate the hosted service.
                logger.LogWarning(error, "Managed Agent install-job finalization cycle failed");
            }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        } while (!stoppingToken.IsCancellationRequested);
    }
}
