namespace LlmProxy.UpdateAgent;

public sealed record UpdateJob(
    Guid Id,
    string Version,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ScheduledForUtc,
    string Status,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? Error = null,
    int? ExitCode = null);

public sealed record UpdateAgentSnapshot(
    string? InstalledVersion,
    UpdateJob? ActiveJob,
    IReadOnlyList<UpdateJob> RecentJobs);

public sealed record ScheduleUpdateRequest(
    string Version,
    DateTimeOffset? ScheduledForUtc = null,
    bool Force = false);
