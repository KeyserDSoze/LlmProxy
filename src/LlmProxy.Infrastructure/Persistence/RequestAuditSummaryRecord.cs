namespace LlmProxy.Infrastructure.Persistence;

public sealed class RequestAuditSummaryRecord
{
    public long Id { get; set; }
    public long ContentLogId { get; set; }
    public string SummaryCiphertext { get; set; } = string.Empty;
    public string LogicalModel { get; set; } = string.Empty;
    public Guid? NodeId { get; set; }
    public Guid? DeploymentId { get; set; }
    public string GeneratedBy { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
