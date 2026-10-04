namespace LlmProxy.Infrastructure.Persistence;

public sealed class InferenceContentLogRecord
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public string Surface { get; set; } = "unknown";
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = string.Empty;
    public string? LogicalModel { get; set; }
    public Guid? ApiCredentialId { get; set; }
    public int StatusCode { get; set; }
    public string? RequestContentType { get; set; }
    public string? ResponseContentType { get; set; }
    public string RequestBodyCiphertext { get; set; } = string.Empty;
    public string ResponseBodyCiphertext { get; set; } = string.Empty;
    public RequestAuditSummaryRecord? Summary { get; set; }
}
