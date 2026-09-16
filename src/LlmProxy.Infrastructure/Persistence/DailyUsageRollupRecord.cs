namespace LlmProxy.Infrastructure.Persistence;

public sealed class DailyUsageRollupRecord
{
    public DateOnly DayUtc { get; set; }
    public Guid ApiCredentialId { get; set; }
    public Guid UsageGroupId { get; set; }
    public string LogicalModel { get; set; } = string.Empty;
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
}
