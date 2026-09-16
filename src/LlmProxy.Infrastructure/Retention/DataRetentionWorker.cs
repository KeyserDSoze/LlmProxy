using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Retention;

public sealed class DataRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DataRetentionWorker> logger) : BackgroundService
{
    private readonly DataRetentionSettings _settings = DataRetentionSettings.FromConfiguration(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            logger.LogInformation("Data retention background cleanup is disabled.");
            return;
        }

        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(_settings.IntervalHours));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<DataRetentionService>();
            var result = await service.RunAsync(cancellationToken: cancellationToken);

            logger.LogInformation(
                "Data retention cleanup completed. RolledUpRequestMetricDays={RolledUpRequestMetricDays} DeletedRequestMetrics={DeletedRequestMetrics} DeletedUsageRollups={DeletedUsageRollups} DeletedAuditEvents={DeletedAuditEvents} DeletedRuntimeStateOutbox={DeletedRuntimeStateOutbox} RequestMetricsCutoffUtc={RequestMetricsCutoffUtc} UsageRollupsCutoffUtc={UsageRollupsCutoffUtc}.",
                result.RolledUpRequestMetricDays,
                result.DeletedRequestMetrics,
                result.DeletedUsageRollups,
                result.DeletedAuditEvents,
                result.DeletedRuntimeStateOutbox,
                result.RequestMetricsCutoffUtc,
                result.UsageRollupsCutoffUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Data retention cleanup iteration failed.");
        }
    }
}
