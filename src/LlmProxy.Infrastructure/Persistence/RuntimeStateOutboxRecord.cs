namespace LlmProxy.Infrastructure.Persistence;

public sealed class RuntimeStateOutboxRecord
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public string? LastError { get; set; }
}