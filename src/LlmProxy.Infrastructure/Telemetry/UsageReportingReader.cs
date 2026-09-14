using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class UsageReportingReader(GatewayDbContext dbContext)
{
    public async Task<UsageReport> ReadAsync(int requestedDays, CancellationToken cancellationToken)
    {
        var windowDays = Math.Clamp(requestedDays, 1, 365);
        var sinceUtc = DateTimeOffset.UtcNow.AddDays(-windowDays);
        var query = dbContext.RequestMetrics.AsNoTracking().Where(metric => metric.StartedAtUtc >= sinceUtc);

        var totals = await query
            .GroupBy(_ => 1)
            .Select(group => new UsageTotals(
                group.LongCount(),
                group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded"),
                group.LongCount(metric => metric.ErrorCode == "capacity_exhausted")))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new UsageTotals(0, 0, 0, 0, 0, 0, 0);

        // Keep aggregation in PostgreSQL. Materialize only aggregate rows before mapping to
        // domain-facing records: EF/Npgsql cannot reliably translate OrderBy over a custom
        // record constructor that also contains nullable Average expressions.
        var groupAggregates = await query
            .GroupBy(metric => metric.UsageGroupId)
            .Select(group => new
            {
                UsageGroupId = group.Key,
                RequestCount = group.LongCount(),
                ErrorCount = group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                InputTokens = group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                OutputTokens = group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                TotalTokens = group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                RateLimitedRequests = group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded"),
                AverageTtftMilliseconds = group.Average(metric => (double?)metric.TimeToFirstByteMilliseconds),
                AverageDurationMilliseconds = group.Average(metric => (double)metric.DurationMilliseconds)
            })
            .ToListAsync(cancellationToken);

        var groupRows = groupAggregates
            .OrderByDescending(row => row.RequestCount)
            .Select(row => new UsageGroupAggregate(
                row.UsageGroupId,
                row.RequestCount,
                row.ErrorCount,
                row.InputTokens,
                row.OutputTokens,
                row.TotalTokens,
                row.RateLimitedRequests,
                row.AverageTtftMilliseconds,
                row.AverageDurationMilliseconds))
            .ToList();

        var credentialAggregates = await query
            .Where(metric => metric.ApiCredentialId != null)
            .GroupBy(metric => new { metric.ApiCredentialId, metric.UsageGroupId })
            .Select(group => new
            {
                ApiCredentialId = group.Key.ApiCredentialId!.Value,
                group.Key.UsageGroupId,
                RequestCount = group.LongCount(),
                ErrorCount = group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                InputTokens = group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                OutputTokens = group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                TotalTokens = group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                RateLimitedRequests = group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded")
            })
            .ToListAsync(cancellationToken);

        var credentialRows = credentialAggregates
            .OrderByDescending(row => row.RequestCount)
            .Select(row => new UsageCredentialAggregate(
                row.ApiCredentialId,
                row.UsageGroupId,
                row.RequestCount,
                row.ErrorCount,
                row.InputTokens,
                row.OutputTokens,
                row.TotalTokens,
                row.RateLimitedRequests))
            .ToList();

        var modelAggregates = await query
            .GroupBy(metric => metric.LogicalModel)
            .Select(group => new
            {
                LogicalModel = group.Key,
                RequestCount = group.LongCount(),
                ErrorCount = group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                InputTokens = group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                OutputTokens = group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                TotalTokens = group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                RateLimitedRequests = group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded")
            })
            .ToListAsync(cancellationToken);

        var modelRows = modelAggregates
            .OrderByDescending(row => row.RequestCount)
            .Select(row => new UsageModelAggregate(
                row.LogicalModel,
                row.RequestCount,
                row.ErrorCount,
                row.InputTokens,
                row.OutputTokens,
                row.TotalTokens,
                row.RateLimitedRequests))
            .ToList();

        var usageGroupNames = await dbContext.UsageGroups.AsNoTracking()
            .ToDictionaryAsync(group => group.Id, group => group.Name, cancellationToken);
        var credentials = await dbContext.ApiCredentials.AsNoTracking()
            .Select(credential => new { credential.Id, credential.Name, credential.KeyPrefix })
            .ToDictionaryAsync(credential => credential.Id, cancellationToken);

        var groups = groupRows.Select(row => new UsageGroupSummary(
            row.UsageGroupId,
            row.UsageGroupId is Guid groupId && usageGroupNames.TryGetValue(groupId, out var groupName) ? groupName : "Ungrouped",
            row.RequestCount,
            row.ErrorCount,
            row.InputTokens,
            row.OutputTokens,
            row.TotalTokens,
            row.RateLimitedRequests,
            row.AverageTtftMilliseconds,
            row.AverageDurationMilliseconds)).ToList();

        var credentialSummaries = credentialRows.Select(row =>
        {
            credentials.TryGetValue(row.ApiCredentialId, out var credential);
            return new UsageCredentialSummary(
                row.ApiCredentialId,
                credential?.Name ?? "Unknown credential",
                credential?.KeyPrefix,
                row.UsageGroupId,
                row.RequestCount,
                row.ErrorCount,
                row.InputTokens,
                row.OutputTokens,
                row.TotalTokens,
                row.RateLimitedRequests);
        }).ToList();

        return new UsageReport(
            windowDays,
            sinceUtc,
            totals.RequestCount,
            totals.ErrorCount,
            totals.InputTokens,
            totals.OutputTokens,
            totals.TotalTokens,
            totals.RateLimitedRequests,
            totals.CapacityExhaustedRequests,
            groups,
            credentialSummaries,
            modelRows.Select(row => new UsageModelSummary(
                row.LogicalModel,
                row.RequestCount,
                row.ErrorCount,
                row.InputTokens,
                row.OutputTokens,
                row.TotalTokens,
                row.RateLimitedRequests)).ToList());
    }

    private sealed record UsageTotals(
        long RequestCount,
        long ErrorCount,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        long RateLimitedRequests,
        long CapacityExhaustedRequests);

    private sealed record UsageGroupAggregate(
        Guid? UsageGroupId,
        long RequestCount,
        long ErrorCount,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        long RateLimitedRequests,
        double? AverageTtftMilliseconds,
        double? AverageDurationMilliseconds);

    private sealed record UsageCredentialAggregate(
        Guid ApiCredentialId,
        Guid? UsageGroupId,
        long RequestCount,
        long ErrorCount,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        long RateLimitedRequests);

    private sealed record UsageModelAggregate(
        string LogicalModel,
        long RequestCount,
        long ErrorCount,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        long RateLimitedRequests);
}

public sealed record UsageReport(
    int WindowDays,
    DateTimeOffset SinceUtc,
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
