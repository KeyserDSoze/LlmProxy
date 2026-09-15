using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Runtime;

public sealed record RuntimeStateOutboxDiagnostics(
    int PendingCount,
    int FailedPendingCount,
    DateTimeOffset? OldestPendingAtUtc,
    long? OldestPendingAgeSeconds,
    int MaxPendingAttemptCount,
    DateTimeOffset? LastProcessedAtUtc,
    string? LastError);

public sealed class RuntimeStateOutboxDiagnosticsReader(GatewayDbContext dbContext)
{
    public async Task<RuntimeStateOutboxDiagnostics> ReadAsync(
        DateTimeOffset? nowUtc = null,
        CancellationToken cancellationToken = default)
    {
        var observedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
        var pendingQuery = dbContext.RuntimeStateOutbox
            .AsNoTracking()
            .Where(record => record.ProcessedAtUtc == null);

        var pendingCount = await pendingQuery.CountAsync(cancellationToken);
        if (pendingCount == 0)
        {
            var lastProcessedAtUtc = await dbContext.RuntimeStateOutbox
                .AsNoTracking()
                .Where(record => record.ProcessedAtUtc != null)
                .OrderByDescending(record => record.ProcessedAtUtc)
                .Select(record => record.ProcessedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            return new RuntimeStateOutboxDiagnostics(
                0,
                0,
                null,
                null,
                0,
                lastProcessedAtUtc,
                null);
        }

        var oldestPendingAtUtc = await pendingQuery
            .OrderBy(record => record.Id)
            .Select(record => (DateTimeOffset?)record.OccurredAtUtc)
            .FirstAsync(cancellationToken);

        var failedPendingCount = await pendingQuery
            .CountAsync(record => record.AttemptCount > 0, cancellationToken);

        var maxPendingAttemptCount = await pendingQuery
            .OrderByDescending(record => record.AttemptCount)
            .Select(record => record.AttemptCount)
            .FirstAsync(cancellationToken);

        var lastError = await pendingQuery
            .Where(record => record.LastError != null)
            .OrderByDescending(record => record.Id)
            .Select(record => record.LastError)
            .FirstOrDefaultAsync(cancellationToken);

        var lastProcessed = await dbContext.RuntimeStateOutbox
            .AsNoTracking()
            .Where(record => record.ProcessedAtUtc != null)
            .OrderByDescending(record => record.ProcessedAtUtc)
            .Select(record => record.ProcessedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        long? oldestAge = oldestPendingAtUtc is DateTimeOffset oldest
            ? Math.Max(0L, (long)Math.Floor((observedAtUtc - oldest).TotalSeconds))
            : null;

        return new RuntimeStateOutboxDiagnostics(
            pendingCount,
            failedPendingCount,
            oldestPendingAtUtc,
            oldestAge,
            maxPendingAttemptCount,
            lastProcessed,
            lastError);
    }
}
