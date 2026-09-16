using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Retention;

public sealed class DataRetentionService(
    GatewayDbContext dbContext,
    IConfiguration configuration)
{
    public DataRetentionSettings Settings { get; } = DataRetentionSettings.FromConfiguration(configuration);

    public async Task<DataRetentionResult> RunAsync(
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
        var requestMetricsCutoffUtc = StartOfUtcDay(startedAtUtc.AddDays(-Settings.RequestMetricsDays));
        var usageRollupsCutoffUtc = StartOfUtcDay(startedAtUtc.AddDays(-Settings.UsageRollupsDays));
        var auditEventsCutoffUtc = startedAtUtc.AddDays(-Settings.AuditEventsDays);
        var runtimeStateOutboxCutoffUtc = startedAtUtc.AddDays(-Settings.RuntimeStateOutboxDays);

        var (rolledUpRequestMetricDays, deletedRequestMetrics) =
            await RollupAndDeleteRequestMetricsAsync(requestMetricsCutoffUtc, cancellationToken);
        var deletedUsageRollups = await DeleteUsageRollupsAsync(
            DateOnly.FromDateTime(usageRollupsCutoffUtc.UtcDateTime),
            cancellationToken);
        var deletedAuditEvents = await DeleteAuditEventsAsync(auditEventsCutoffUtc, cancellationToken);
        var deletedRuntimeStateOutbox = await DeleteRuntimeStateOutboxAsync(runtimeStateOutboxCutoffUtc, cancellationToken);

        return new DataRetentionResult(
            startedAtUtc,
            DateTimeOffset.UtcNow,
            requestMetricsCutoffUtc,
            usageRollupsCutoffUtc,
            auditEventsCutoffUtc,
            runtimeStateOutboxCutoffUtc,
            rolledUpRequestMetricDays,
            deletedRequestMetrics,
            deletedUsageRollups,
            deletedAuditEvents,
            deletedRuntimeStateOutbox);
    }

    private async Task<(int RolledUpDays, int DeletedMetrics)> RollupAndDeleteRequestMetricsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var rolledUpDays = 0;
        var deletedMetrics = 0;

        while (true)
        {
            var earliest = await dbContext.RequestMetrics
                .AsNoTracking()
                .Where(metric => metric.StartedAtUtc < cutoffUtc)
                .OrderBy(metric => metric.StartedAtUtc)
                .Select(metric => (DateTimeOffset?)metric.StartedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (earliest is null)
            {
                return (rolledUpDays, deletedMetrics);
            }

            var dayStartUtc = StartOfUtcDay(earliest.Value);
            var dayEndUtc = dayStartUtc.AddDays(1);
            var dayUtc = DateOnly.FromDateTime(dayStartUtc.UtcDateTime);

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(7301026091616001)",
                cancellationToken);

            // Another replica may have compacted this day while this transaction waited for the lock.
            // Re-query inside the lock and simply continue when no raw rows remain.
            var aggregates = await dbContext.RequestMetrics
                .AsNoTracking()
                .Where(metric => metric.StartedAtUtc >= dayStartUtc && metric.StartedAtUtc < dayEndUtc)
                .GroupBy(metric => new
                {
                    ApiCredentialId = metric.ApiCredentialId ?? Guid.Empty,
                    UsageGroupId = metric.UsageGroupId ?? Guid.Empty,
                    metric.LogicalModel
                })
                .Select(group => new
                {
                    group.Key.ApiCredentialId,
                    group.Key.UsageGroupId,
                    group.Key.LogicalModel,
                    RequestCount = group.LongCount(),
                    ErrorCount = group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                    InputTokens = group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                    OutputTokens = group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                    TotalTokens = group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                    RateLimitedRequests = group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded"),
                    CapacityExhaustedRequests = group.LongCount(metric => metric.ErrorCode == "capacity_exhausted"),
                    DurationMillisecondsTotal = group.Sum(metric => metric.DurationMilliseconds),
                    TtftMillisecondsTotal = group.Sum(metric => metric.TimeToFirstByteMilliseconds ?? 0L),
                    TtftSampleCount = group.LongCount(metric => metric.TimeToFirstByteMilliseconds != null)
                })
                .ToListAsync(cancellationToken);

            if (aggregates.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                continue;
            }

            await dbContext.DailyUsageRollups
                .Where(rollup => rollup.DayUtc == dayUtc)
                .ExecuteDeleteAsync(cancellationToken);

            dbContext.DailyUsageRollups.AddRange(aggregates.Select(row => new DailyUsageRollupRecord
            {
                DayUtc = dayUtc,
                ApiCredentialId = row.ApiCredentialId,
                UsageGroupId = row.UsageGroupId,
                LogicalModel = row.LogicalModel,
                RequestCount = row.RequestCount,
                ErrorCount = row.ErrorCount,
                InputTokens = row.InputTokens,
                OutputTokens = row.OutputTokens,
                TotalTokens = row.TotalTokens,
                RateLimitedRequests = row.RateLimitedRequests,
                CapacityExhaustedRequests = row.CapacityExhaustedRequests,
                DurationMillisecondsTotal = row.DurationMillisecondsTotal,
                TtftMillisecondsTotal = row.TtftMillisecondsTotal,
                TtftSampleCount = row.TtftSampleCount
            }));
            await dbContext.SaveChangesAsync(cancellationToken);

            deletedMetrics += await dbContext.RequestMetrics
                .Where(metric => metric.StartedAtUtc >= dayStartUtc && metric.StartedAtUtc < dayEndUtc)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            rolledUpDays++;
            dbContext.ChangeTracker.Clear();
        }
    }

    private Task<int> DeleteUsageRollupsAsync(
        DateOnly cutoffDayUtc,
        CancellationToken cancellationToken)
        => dbContext.DailyUsageRollups
            .Where(rollup => rollup.DayUtc < cutoffDayUtc)
            .ExecuteDeleteAsync(cancellationToken);

    private async Task<int> DeleteAuditEventsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var ids = await dbContext.AuditEvents
                .AsNoTracking()
                .Where(audit => audit.OccurredAtUtc < cutoffUtc)
                .OrderBy(audit => audit.Id)
                .Select(audit => audit.Id)
                .Take(Settings.BatchSize)
                .ToArrayAsync(cancellationToken);

            if (ids.Length == 0)
            {
                return total;
            }

            total += await dbContext.AuditEvents
                .Where(audit => ids.Contains(audit.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    private async Task<int> DeleteRuntimeStateOutboxAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var ids = await dbContext.RuntimeStateOutbox
                .AsNoTracking()
                .Where(record => record.ProcessedAtUtc != null && record.ProcessedAtUtc < cutoffUtc)
                .OrderBy(record => record.Id)
                .Select(record => record.Id)
                .Take(Settings.BatchSize)
                .ToArrayAsync(cancellationToken);

            if (ids.Length == 0)
            {
                return total;
            }

            total += await dbContext.RuntimeStateOutbox
                .Where(record => ids.Contains(record.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static DateTimeOffset StartOfUtcDay(DateTimeOffset value)
        => new(value.UtcDateTime.Date, TimeSpan.Zero);
}
