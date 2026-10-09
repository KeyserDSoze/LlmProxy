namespace LlmProxy.Application.Abstractions;

public sealed record DeploymentRuntimeMetricsSnapshot(
    Guid DeploymentId, Guid NodeId, bool Available, string? Runtime, string? ModelName,
    double RunningRequests, double WaitingRequests, double? CacheUsageRatio,
    double? PromptTokensTotal, double? GenerationTokensTotal,
    DateTimeOffset? CollectedAtUtc, DateTimeOffset? LastAttemptAtUtc, string? Error);

public interface IDeploymentRuntimeMetricsTracker
{
    DeploymentRuntimeMetricsSnapshot GetSnapshot(Guid deploymentId);
    IReadOnlyList<DeploymentRuntimeMetricsSnapshot> GetSnapshots();
    void RecordSuccess(Guid deploymentId, Guid nodeId, string runtime, string? modelName,
        double running, double waiting, double? cacheUsage,
        double? promptTokens, double? generatedTokens, DateTimeOffset collectedAtUtc);
    void RecordFailure(Guid deploymentId, Guid nodeId, string error, DateTimeOffset attemptedAtUtc);
    void RetainOnly(IReadOnlySet<Guid> currentDeploymentIds);
}
