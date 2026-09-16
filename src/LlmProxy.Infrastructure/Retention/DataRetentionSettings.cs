using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Retention;

public sealed record DataRetentionSettings(
    bool Enabled,
    int RequestMetricsDays,
    int UsageRollupsDays,
    int AuditEventsDays,
    int RuntimeStateOutboxDays,
    int IntervalHours,
    int BatchSize)
{
    public static DataRetentionSettings FromConfiguration(IConfiguration configuration)
        => new(
            configuration.GetValue("Retention:Enabled", true),
            Math.Clamp(configuration.GetValue("Retention:RequestMetricsDays", 90), 1, 3650),
            Math.Clamp(configuration.GetValue("Retention:UsageRollupsDays", 730), 1, 3650),
            Math.Clamp(configuration.GetValue("Retention:AuditEventsDays", 365), 1, 3650),
            Math.Clamp(configuration.GetValue("Retention:RuntimeStateOutboxDays", 30), 1, 3650),
            Math.Clamp(configuration.GetValue("Retention:IntervalHours", 24), 1, 168),
            Math.Clamp(configuration.GetValue("Retention:BatchSize", 5000), 100, 50000));
}

public sealed record DataRetentionResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset RequestMetricsCutoffUtc,
    DateTimeOffset UsageRollupsCutoffUtc,
    DateTimeOffset AuditEventsCutoffUtc,
    DateTimeOffset RuntimeStateOutboxCutoffUtc,
    int RolledUpRequestMetricDays,
    int DeletedRequestMetrics,
    int DeletedUsageRollups,
    int DeletedAuditEvents,
    int DeletedRuntimeStateOutbox);
