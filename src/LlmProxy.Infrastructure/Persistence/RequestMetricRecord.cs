namespace LlmProxy.Infrastructure.Persistence;

public sealed class RequestMetricRecord
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public string LogicalModel { get; set; } = string.Empty;
    public string Surface { get; set; } = "unknown";
    public Guid? DeploymentId { get; set; }
    public Guid? NodeId { get; set; }
    public Guid? ApiCredentialId { get; set; }
    public int StatusCode { get; set; }
    public long DurationMilliseconds { get; set; }
    public int AttemptCount { get; set; } = 1;
    public bool IsStreaming { get; set; }
    public long? UpstreamHeaderMilliseconds { get; set; }
    public long? TimeToFirstByteMilliseconds { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public string? ErrorCode { get; set; }
}
