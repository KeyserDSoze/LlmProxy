namespace LlmProxy.Application.Abstractions;

public sealed record GatewayRequestMetric(
    Guid RequestId,
    DateTimeOffset StartedAtUtc,
    string LogicalModel,
    string Surface,
    Guid? DeploymentId,
    Guid? NodeId,
    Guid? ApiCredentialId,
    Guid? UsageGroupId,
    int StatusCode,
    long DurationMilliseconds,
    int AttemptCount,
    bool IsStreaming,
    long? UpstreamHeaderMilliseconds,
    long? TimeToFirstByteMilliseconds,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    string? ErrorCode);

public interface IRequestMetricsSink
{
    void Write(GatewayRequestMetric metric);
}
