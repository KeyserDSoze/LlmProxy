namespace LlmProxy.Infrastructure.Persistence;

/// <summary>
/// How one serving engine (a Docker image such as llama.cpp or vLLM) is kept up to date on one server. Without a row the
/// engine is only updated by hand. The scheduled time is a wall-clock time in an IANA time zone, so it follows daylight saving.
/// </summary>
public sealed class RuntimeEnginePolicyRecord
{
    public Guid NodeId { get; set; }
    public string EngineId { get; set; } = string.Empty;
    public bool AutoUpdate { get; set; }
    /// <summary>Local time of day, <c>HH:mm</c>.</summary>
    public string UpdateAtLocal { get; set; } = "03:00";
    public string TimeZone { get; set; } = "UTC";
    /// <summary>The last time a replica claimed the schedule; gates retries so replicas never act twice.</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    /// <summary>The last scheduled run that reached the Agent (finished, failed or skipped on purpose).</summary>
    public DateTimeOffset? LastRunAtUtc { get; set; }
    /// <summary><c>updated</c>, <c>up_to_date</c>, <c>skipped</c>, <c>failed</c> or <c>waiting</c>.</summary>
    public string? LastResult { get; set; }
    public string? LastMessage { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
