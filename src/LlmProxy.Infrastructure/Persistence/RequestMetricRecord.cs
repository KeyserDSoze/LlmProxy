namespace LlmProxy.Infrastructure.Persistence;

public sealed class RequestMetricRecord
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public string LogicalModel { get; set; } = string.Empty;
    public Guid? DeploymentId { get; set; }
    public Guid? NodeId { get; set; }
    public Guid? ApiCredentialId { get; set; }
    public int StatusCode { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? ErrorCode { get; set; }
}
