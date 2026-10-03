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
    int? ExitCode = null,
    IReadOnlyList<string>? UpgradePath = null,
    string? CurrentStep = null);

public sealed record UpdateAgentSnapshot(
    string? InstalledVersion,
    UpdateJob? ActiveJob,
    IReadOnlyList<UpdateJob> RecentJobs);

public sealed record ScheduleUpdateRequest(
    string Version,
    DateTimeOffset? ScheduledForUtc = null,
    bool Force = false,
    IReadOnlyList<string>? Versions = null);
