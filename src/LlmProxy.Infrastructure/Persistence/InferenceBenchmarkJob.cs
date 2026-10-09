namespace LlmProxy.Infrastructure.Persistence;

/// <summary>Persistent control-plane benchmark job; contains ONLY synthetic-prompt measurements, never prompt bodies or secrets.</summary>
public sealed class InferenceBenchmarkJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeploymentId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public double MaxP95TtftMilliseconds { get; set; } = 5000;
    public double MinSuccessRatePercent { get; set; } = 99;
    public string? ReportJson { get; set; }
    public string? Error { get; set; }
}
