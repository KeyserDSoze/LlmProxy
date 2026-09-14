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

        var groupRows = await query
            .GroupBy(metric => metric.UsageGroupId)
            .Select(group => new UsageGroupAggregate(
                group.Key,
                group.LongCount(),
                group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded"),
                group.Average(metric => (double?)metric.TimeToFirstByteMilliseconds),
                group.Average(metric => (double?)metric.DurationMilliseconds)))
            .OrderByDescending(row => row.RequestCount)
            .ToListAsync(cancellationToken);

        var credentialRows = await query
            .Where(metric => metric.ApiCredentialId != null)
            .GroupBy(metric => new { metric.ApiCredentialId, metric.UsageGroupId })
            .Select(group => new UsageCredentialAggregate(
                group.Key.ApiCredentialId!.Value,
                group.Key.UsageGroupId,
                group.LongCount(),
                group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded")))
            .OrderByDescending(row => row.RequestCount)
            .ToListAsync(cancellationToken);

        var modelRows = await query
            .GroupBy(metric => metric.LogicalModel)
            .Select(group => new UsageModelAggregate(
                group.Key,
                group.LongCount(),
                group.LongCount(metric => metric.StatusCode < 200 || metric.StatusCode >= 400),
                group.Sum(metric => (long)(metric.InputTokens ?? 0)),
                group.Sum(metric => (long)(metric.OutputTokens ?? 0)),
                group.Sum(metric => (long)(metric.TotalTokens ?? 0)),
                group.LongCount(metric => metric.ErrorCode == "rate_limit_exceeded")))
            .OrderByDescending(row => row.RequestCount)
            .ToListAsync(cancellationToken);

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
