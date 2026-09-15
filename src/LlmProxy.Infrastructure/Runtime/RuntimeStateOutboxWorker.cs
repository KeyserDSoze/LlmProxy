using System.Data.Common;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Runtime;

public sealed class RuntimeStateOutboxWorker(
    IServiceScopeFactory scopeFactory,
    IRuntimeStateDurablePublisher publisher,
    IConfiguration configuration,
    ILogger<RuntimeStateOutboxWorker> logger) : BackgroundService
{
    private const long AdvisoryLockKey = 0x4C4C4D50; // "LLMP"
    private readonly int _batchSize = Math.Clamp(configuration.GetValue("Redis:OutboxBatchSize", 50), 1, 500);
    private readonly int _pollMilliseconds = Math.Clamp(configuration.GetValue("Redis:OutboxPollMilliseconds", 500), 100, 5000);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processedAny = false;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                if (await TryAcquirePublisherLockAsync(dbContext, stoppingToken))
                {
                    try
                    {
                        processedAny = await ProcessBatchAsync(dbContext, stoppingToken);
                    }
                    finally
                    {
                        await ReleasePublisherLockAsync(dbContext);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Runtime-state outbox iteration failed; it will be retried.");
            }

            if (!processedAny)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_pollMilliseconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> ProcessBatchAsync(GatewayDbContext dbContext, CancellationToken cancellationToken)
    {
        var records = await dbContext.RuntimeStateOutbox
            .Where(record => record.ProcessedAtUtc == null)
            .OrderBy(record => record.Id)
            .Take(_batchSize)
            .ToListAsync(cancellationToken);

        if (records.Count == 0)
        {
            return false;
        }

        var processedAny = false;
        foreach (var record in records)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            if (record.NextAttemptAtUtc is DateTimeOffset nextAttemptAtUtc && nextAttemptAtUtc > nowUtc)
            {
                // Preserve global mutation order: never publish a later event while the oldest pending event is backing off.
                break;
            }

            try
            {
                await publisher.PublishAsync(
                    new RuntimeStateChange(
                        record.Kind,
                        record.Action,
                        record.EntityId,
                        record.PayloadJson,
                        record.OccurredAtUtc),
                    cancellationToken);

                record.AttemptCount++;
                record.ProcessedAtUtc = DateTimeOffset.UtcNow;
                record.NextAttemptAtUtc = null;
                record.LastError = null;
                await dbContext.SaveChangesAsync(cancellationToken);
                processedAny = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                record.AttemptCount++;
                record.NextAttemptAtUtc = DateTimeOffset.UtcNow + RetryDelay(record.AttemptCount);
                record.LastError = Truncate(exception.Message, 2000);
                await dbContext.SaveChangesAsync(cancellationToken);

                logger.LogWarning(
                    exception,
                    "Runtime-state outbox event {OutboxId} ({Kind}/{Action}) failed on attempt {AttemptCount}; later events remain blocked until retry.",
                    record.Id,
                    record.Kind,
                    record.Action,
                    record.AttemptCount);
                processedAny = true;
                break;
            }
        }

        return processedAny;
    }

    private static TimeSpan RetryDelay(int attemptCount)
    {
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 6);
        return TimeSpan.FromSeconds(Math.Pow(2, exponent));
    }

    private static async Task<bool> TryAcquirePublisherLockAsync(
        GatewayDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockKey})";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    private static async Task ReleasePublisherLockAsync(GatewayDbContext dbContext)
    {
        try
        {
            await using DbCommand command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"SELECT pg_advisory_unlock({AdvisoryLockKey})";
            await command.ExecuteScalarAsync();
        }
        catch
        {
            // Closing/disposing the PostgreSQL connection also releases a session advisory lock.
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}