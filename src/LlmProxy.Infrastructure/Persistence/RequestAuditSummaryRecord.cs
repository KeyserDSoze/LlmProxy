using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Persistence;

[Table("request_audit_summaries")]
[Index(nameof(ContentLogId), IsUnique = true)]
[Index(nameof(UpdatedAtUtc))]
public sealed class RequestAuditSummaryRecord
{
    [Key]
    public long Id { get; set; }

    public long ContentLogId { get; set; }

    [Column(TypeName = "text")]
    public string SummaryCiphertext { get; set; } = string.Empty;

    [MaxLength(160)]
    public string LogicalModel { get; set; } = string.Empty;

    public Guid? NodeId { get; set; }
    public Guid? DeploymentId { get; set; }

    [MaxLength(320)]
    public string GeneratedBy { get; set; } = string.Empty;

    public DateTimeOffset GeneratedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [ForeignKey(nameof(ContentLogId))]
    public InferenceContentLogRecord ContentLog { get; set; } = null!;
}
