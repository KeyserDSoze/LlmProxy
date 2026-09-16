using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class UsageReportingReader(
    GatewayDbContext dbContext,
    IConfiguration configuration)
{
    public async Task<UsageReport> ReadAsync(int requestedDays, CancellationToken cancellationToken)
    {
        var settings = DataRetentionSettings.FromConfiguration(configuration);
        var maxWindowDays = Math.Max(settings.RequestMetricsDays, settings.UsageRollupsDays);
        var windowDays = Math.Clamp(requestedDays, 1, maxWindowDays);
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        var sinceDayUtc = todayUtc.AddDays(-(windowDays - 1));
        var untilDayUtc = todayUtc.AddDays(1);
        var sinceUtc = new DateTimeOffset(sinceDayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var untilUtc = new DateTimeOffset(untilDayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        var rawQuery = dbContext.RequestMetrics.AsNoTracking()
            .Where(metric => metric.StartedAtUtc >= sinceUtc && metric.StartedAtUtc < untilUtc);
        var rollupQuery = dbContext.DailyUsageRollups.AsNoTracking()
            .Where(rollup => rollup.DayUtc >= sinceDayUtc && rollup.DayUtc < untilDayUtc);

        var rawTotals = await rawQuery
            .GroupBy(_ => 1)
            .Select(group => new
            {
                RequestCount = group.LongCount(),
                ErrorCount = group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                InputTokens = group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                OutputTokens = group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                TotalTokens = group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                RateLimitedRequests = group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded"),
                CapacityExhaustedRequests = group.LongCount(metric => metric.ErrorCode == "capacity_exhausted")
            })
            .SingleOrDefaultAsync(cancellationToken);

        var rollupTotals = await rollupQuery
            .GroupBy(_ => 1)
            .Select(group => new
            {
                RequestCount = group.Sum(rollup => rollup.RequestCount),
                ErrorCount = group.Sum(rollup => rollup.ErrorCount),
                InputTokens = group.Sum(rollup => rollup.InputTokens),
                OutputTokens = group.Sum(rollup => rollup.OutputTokens),
                TotalTokens = group.Sum(rollup => rollup.TotalTokens),
                RateLimitedRequests = group.Sum(rollup => rollup.RateLimitedRequests),
                CapacityExhaustedRequests = group.Sum(rollup => rollup.CapacityExhaustedRequests)
            })
            .SingleOrDefaultAsync(cancellationToken);

        var rawRequestCount = rawTotals?.RequestCount ?? 0;
        var rolledUpRequestCount = rollupTotals?.RequestCount ?? 0;

        var groupAggregates = new Dictionary<Guid, AggregateAccumulator>();
        var rawGroupRows = await rawQuery
            .GroupBy(metric => metric.UsageGroupId ?? Guid.Empty)
            .Select(group => new
            {
                UsageGroupId = group.Key,
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
        foreach (var row in rawGroupRows)
        {
            AddAggregate(groupAggregates, row.UsageGroupId, row.RequestCount, row.ErrorCount, row.InputTokens,
                row.OutputTokens, row.TotalTokens, row.RateLimitedRequests, row.CapacityExhaustedRequests,
                row.DurationMillisecondsTotal, row.TtftMillisecondsTotal, row.TtftSampleCount);
        }

        var rollupGroupRows = await rollupQuery
            .GroupBy(rollup => rollup.UsageGroupId)
            .Select(group => new
            {
                UsageGroupId = group.Key,
                RequestCount = group.Sum(rollup => rollup.RequestCount),
                ErrorCount = group.Sum(rollup => rollup.ErrorCount),
                InputTokens = group.Sum(rollup => rollup.InputTokens),
                OutputTokens = group.Sum(rollup => rollup.OutputTokens),
                TotalTokens = group.Sum(rollup => rollup.TotalTokens),
                RateLimitedRequests = group.Sum(rollup => rollup.RateLimitedRequests),
                CapacityExhaustedRequests = group.Sum(rollup => rollup.CapacityExhaustedRequests),
                DurationMillisecondsTotal = group.Sum(rollup => rollup.DurationMillisecondsTotal),
                TtftMillisecondsTotal = group.Sum(rollup => rollup.TtftMillisecondsTotal),
                TtftSampleCount = group.Sum(rollup => rollup.TtftSampleCount)
            })
            .ToListAsync(cancellationToken);
        foreach (var row in rollupGroupRows)
        {
            AddAggregate(groupAggregates, row.UsageGroupId, row.RequestCount, row.ErrorCount, row.InputTokens,
                row.OutputTokens, row.TotalTokens, row.RateLimitedRequests, row.CapacityExhaustedRequests,
                row.DurationMillisecondsTotal, row.TtftMillisecondsTotal, row.TtftSampleCount);
        }

        var credentialAggregates = new Dictionary<(Guid CredentialId, Guid UsageGroupId), AggregateAccumulator>();
        var rawCredentialRows = await rawQuery
            .Where(metric => metric.ApiCredentialId != null)
            .GroupBy(metric => new
            {
                ApiCredentialId = metric.ApiCredentialId!.Value,
                UsageGroupId = metric.UsageGroupId ?? Guid.Empty
            })
            .Select(group => new
            {
                group.Key.ApiCredentialId,
                group.Key.UsageGroupId,
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
        foreach (var row in rawCredentialRows)
        {
            AddAggregate(credentialAggregates, (row.ApiCredentialId, row.UsageGroupId), row.RequestCount,
                row.ErrorCount, row.InputTokens, row.OutputTokens, row.TotalTokens, row.RateLimitedRequests,
                row.CapacityExhaustedRequests, row.DurationMillisecondsTotal, row.TtftMillisecondsTotal,
                row.TtftSampleCount);
        }

        var rollupCredentialRows = await rollupQuery
            .Where(rollup => rollup.ApiCredentialId != Guid.Empty)
            .GroupBy(rollup => new { rollup.ApiCredentialId, rollup.UsageGroupId })
            .Select(group => new
            {
                group.Key.ApiCredentialId,
                group.Key.UsageGroupId,
                RequestCount = group.Sum(rollup => rollup.RequestCount),
                ErrorCount = group.Sum(rollup => rollup.ErrorCount),
                InputTokens = group.Sum(rollup => rollup.InputTokens),
                OutputTokens = group.Sum(rollup => rollup.OutputTokens),
                TotalTokens = group.Sum(rollup => rollup.TotalTokens),
                RateLimitedRequests = group.Sum(rollup => rollup.RateLimitedRequests),
                CapacityExhaustedRequests = group.Sum(rollup => rollup.CapacityExhaustedRequests),
                DurationMillisecondsTotal = group.Sum(rollup => rollup.DurationMillisecondsTotal),
                TtftMillisecondsTotal = group.Sum(rollup => rollup.TtftMillisecondsTotal),
                TtftSampleCount = group.Sum(rollup => rollup.TtftSampleCount)
            })
            .ToListAsync(cancellationToken);
        foreach (var row in rollupCredentialRows)
        {
            AddAggregate(credentialAggregates, (row.ApiCredentialId, row.UsageGroupId), row.RequestCount,
                row.ErrorCount, row.InputTokens, row.OutputTokens, row.TotalTokens, row.RateLimitedRequests,
                row.CapacityExhaustedRequests, row.DurationMillisecondsTotal, row.TtftMillisecondsTotal,
                row.TtftSampleCount);
        }

        var modelAggregates = new Dictionary<string, AggregateAccumulator>(StringComparer.Ordinal);
        var rawModelRows = await rawQuery
            .GroupBy(metric => metric.LogicalModel)
            .Select(group => new
            {
                LogicalModel = group.Key,
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
        foreach (var row in rawModelRows)
        {
            AddAggregate(modelAggregates, row.LogicalModel, row.RequestCount, row.ErrorCount, row.InputTokens,
                row.OutputTokens, row.TotalTokens, row.RateLimitedRequests, row.CapacityExhaustedRequests,
                row.DurationMillisecondsTotal, row.TtftMillisecondsTotal, row.TtftSampleCount);
        }

        var rollupModelRows = await rollupQuery
            .GroupBy(rollup => rollup.LogicalModel)
            .Select(group => new
            {
                LogicalModel = group.Key,
                RequestCount = group.Sum(rollup => rollup.RequestCount),
                ErrorCount = group.Sum(rollup => rollup.ErrorCount),
                InputTokens = group.Sum(rollup => rollup.InputTokens),
                OutputTokens = group.Sum(rollup => rollup.OutputTokens),
                TotalTokens = group.Sum(rollup => rollup.TotalTokens),
                RateLimitedRequests = group.Sum(rollup => rollup.RateLimitedRequests),
                CapacityExhaustedRequests = group.Sum(rollup => rollup.CapacityExhaustedRequests),
                DurationMillisecondsTotal = group.Sum(rollup => rollup.DurationMillisecondsTotal),
                TtftMillisecondsTotal = group.Sum(rollup => rollup.TtftMillisecondsTotal),
                TtftSampleCount = group.Sum(rollup => rollup.TtftSampleCount)
            })
            .ToListAsync(cancellationToken);
        foreach (var row in rollupModelRows)
        {
            AddAggregate(modelAggregates, row.LogicalModel, row.RequestCount, row.ErrorCount, row.InputTokens,
                row.OutputTokens, row.TotalTokens, row.RateLimitedRequests, row.CapacityExhaustedRequests,
                row.DurationMillisecondsTotal, row.TtftMillisecondsTotal, row.TtftSampleCount);
        }

        var usageGroupNames = await dbContext.UsageGroups.AsNoTracking()
            .ToDictionaryAsync(group => group.Id, group => group.Name, cancellationToken);
        var credentials = await dbContext.ApiCredentials.AsNoTracking()
            .Select(credential => new { credential.Id, credential.Name, credential.KeyPrefix })
            .ToDictionaryAsync(credential => credential.Id, cancellationToken);

        var groups = groupAggregates
            .OrderByDescending(pair => pair.Value.RequestCount)
            .Select(pair => new UsageGroupSummary(
                pair.Key == Guid.Empty ? null : pair.Key,
                pair.Key != Guid.Empty && usageGroupNames.TryGetValue(pair.Key, out var groupName) ? groupName : "Ungrouped",
                pair.Value.RequestCount,
                pair.Value.ErrorCount,
                pair.Value.InputTokens,
                pair.Value.OutputTokens,
                pair.Value.TotalTokens,
                pair.Value.RateLimitedRequests,
                pair.Value.AverageTtftMilliseconds,
                pair.Value.AverageDurationMilliseconds))
            .ToList();

        var credentialSummaries = credentialAggregates
            .OrderByDescending(pair => pair.Value.RequestCount)
            .Select(pair =>
            {
                credentials.TryGetValue(pair.Key.CredentialId, out var credential);
                return new UsageCredentialSummary(
                    pair.Key.CredentialId,
                    credential?.Name ?? "Unknown credential",
                    credential?.KeyPrefix,
                    pair.Key.UsageGroupId == Guid.Empty ? null : pair.Key.UsageGroupId,
                    pair.Value.RequestCount,
                    pair.Value.ErrorCount,
                    pair.Value.InputTokens,
                    pair.Value.OutputTokens,
                    pair.Value.TotalTokens,
                    pair.Value.RateLimitedRequests);
            })
            .ToList();

        var models = modelAggregates
            .OrderByDescending(pair => pair.Value.RequestCount)
            .Select(pair => new UsageModelSummary(
                pair.Key,
                pair.Value.RequestCount,
                pair.Value.ErrorCount,
                pair.Value.InputTokens,
                pair.Value.OutputTokens,
                pair.Value.TotalTokens,
                pair.Value.RateLimitedRequests))
            .ToList();

        return new UsageReport(
            windowDays,
            sinceUtc,
            "utc_day",
            settings.RequestMetricsDays,
            settings.UsageRollupsDays,
            rawRequestCount,
            rolledUpRequestCount,
            rolledUpRequestCount > 0,
            rawRequestCount + rolledUpRequestCount,
            (rawTotals?.ErrorCount ?? 0) + (rollupTotals?.ErrorCount ?? 0),
            (rawTotals?.InputTokens ?? 0) + (rollupTotals?.InputTokens ?? 0),
            (rawTotals?.OutputTokens ?? 0) + (rollupTotals?.OutputTokens ?? 0),
            (rawTotals?.TotalTokens ?? 0) + (rollupTotals?.TotalTokens ?? 0),
            (rawTotals?.RateLimitedRequests ?? 0) + (rollupTotals?.RateLimitedRequests ?? 0),
            (rawTotals?.CapacityExhaustedRequests ?? 0) + (rollupTotals?.CapacityExhaustedRequests ?? 0),
            groups,
            credentialSummaries,
            models);
    }

    private static void AddAggregate<TKey>(
        Dictionary<TKey, AggregateAccumulator> aggregates,
        TKey key,
        long requestCount,
        long errorCount,
        long inputTokens,
        long outputTokens,
        long totalTokens,
        long rateLimitedRequests,
        long capacityExhaustedRequests,
        long durationMillisecondsTotal,
        long ttftMillisecondsTotal,
        long ttftSampleCount)
        where TKey : notnull
    {
        if (!aggregates.TryGetValue(key, out var aggregate))
        {
            aggregate = new AggregateAccumulator();
            aggregates.Add(key, aggregate);
        }

        aggregate.RequestCount += requestCount;
        aggregate.ErrorCount += errorCount;
        aggregate.InputTokens += inputTokens;
        aggregate.OutputTokens += outputTokens;
        aggregate.TotalTokens += totalTokens;
        aggregate.RateLimitedRequests += rateLimitedRequests;
        aggregate.CapacityExhaustedRequests += capacityExhaustedRequests;
        aggregate.DurationMillisecondsTotal += durationMillisecondsTotal;
        aggregate.TtftMillisecondsTotal += ttftMillisecondsTotal;
        aggregate.TtftSampleCount += ttftSampleCount;
    }

    private sealed class AggregateAccumulator
    {
        public long RequestCount { get; set; }
        public long ErrorCount { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public long RateLimitedRequests { get; set; }
        public long CapacityExhaustedRequests { get; set; }
        public long DurationMillisecondsTotal { get; set; }
        public long TtftMillisecondsTotal { get; set; }
        public long TtftSampleCount { get; set; }
        public double? AverageTtftMilliseconds => TtftSampleCount == 0 ? null : TtftMillisecondsTotal / (double)TtftSampleCount;
        public double? AverageDurationMilliseconds => RequestCount == 0 ? null : DurationMillisecondsTotal / (double)RequestCount;
    }
}

public sealed record UsageReport(
    int WindowDays,
    DateTimeOffset SinceUtc,
    string WindowGranularity,
    int RawRetentionDays,
    int RollupRetentionDays,
    long RawRequestCount,
    long RolledUpRequestCount,
    bool HistoricalRollupsUsed,
    long RequestCount,
    long ErrorCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long RateLimitedRequests,
    long CapacityExhaustedRequests,
    IReadOnlyList<UsageGroupSummary> Groups,
    IReadOnlyList<UsageCredentialSummary> Credentials,
    IReadOnlyList<UsageModelSummary> Models);

public sealed record UsageGroupSummary(
    Guid? UsageGroupId,
    string Name,
    long RequestCount,
    long ErrorCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long RateLimitedRequests,
    double? AverageTtftMilliseconds,
    double? AverageDurationMilliseconds);

public sealed record UsageCredentialSummary(
    Guid ApiCredentialId,
    string Name,
    string? KeyPrefix,
    Guid? UsageGroupId,
    long RequestCount,
    long ErrorCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long RateLimitedRequests);

public sealed record UsageModelSummary(
    string LogicalModel,
    long RequestCount,
    long ErrorCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long RateLimitedRequests);
