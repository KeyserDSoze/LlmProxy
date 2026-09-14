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
        var requestMetricsCutoffUtc = startedAtUtc.AddDays(-Settings.RequestMetricsDays);
        var auditEventsCutoffUtc = startedAtUtc.AddDays(-Settings.AuditEventsDays);

        var deletedRequestMetrics = await DeleteRequestMetricsAsync(requestMetricsCutoffUtc, cancellationToken);
        var deletedAuditEvents = await DeleteAuditEventsAsync(auditEventsCutoffUtc, cancellationToken);

        return new DataRetentionResult(
            startedAtUtc,
            DateTimeOffset.UtcNow,
            requestMetricsCutoffUtc,
            auditEventsCutoffUtc,
            deletedRequestMetrics,
            deletedAuditEvents);
    }

    private async Task<int> DeleteRequestMetricsAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            var ids = await dbContext.RequestMetrics
                .AsNoTracking()
                .Where(metric => metric.StartedAtUtc < cutoffUtc)
                .OrderBy(metric => metric.Id)
                .Select(metric => metric.Id)
                .Take(Settings.BatchSize)
                .ToArrayAsync(cancellationToken);

            if (ids.Length == 0)
            {
                return total;
            }

            total += await dbContext.RequestMetrics
                .Where(metric => ids.Contains(metric.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

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
}
