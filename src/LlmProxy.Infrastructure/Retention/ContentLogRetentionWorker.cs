using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Retention;

public sealed class ContentLogRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ContentLogRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(ContentLogRetentionService.CleanupIntervalHours));
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
            var service = scope.ServiceProvider.GetRequiredService<ContentLogRetentionService>();
            var result = await service.RunAsync(cancellationToken: cancellationToken);
            logger.LogInformation(
                "Full-body content-log cleanup completed. RetentionDays={RetentionDays} DeletedLogs={DeletedLogs} CutoffUtc={CutoffUtc}.",
                result.RetentionDays,
                result.DeletedLogs,
                result.CutoffUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Full-body content-log cleanup iteration failed.");
        }
    }
}
