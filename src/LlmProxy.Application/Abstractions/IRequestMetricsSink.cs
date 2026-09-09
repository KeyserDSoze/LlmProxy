namespace LlmProxy.Application.Abstractions;

public sealed record GatewayRequestMetric(
    Guid RequestId,
    DateTimeOffset StartedAtUtc,
    string LogicalModel,
    Guid? DeploymentId,
    Guid? NodeId,
    Guid? ApiCredentialId,
    int StatusCode,
    long DurationMilliseconds,
    string? ErrorCode);

public interface IRequestMetricsSink
{
    void Write(GatewayRequestMetric metric);
}
