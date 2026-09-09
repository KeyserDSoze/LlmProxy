namespace LlmProxy.Application.Abstractions;

public sealed record NodeRuntimeMetricsSnapshot(
    Guid NodeId,
    bool Available,
    string? ModelName,
    double RunningRequests,
    double WaitingRequests,
    double? KvCacheUsageRatio,
    double? PromptTokensTotal,
    double? GenerationTokensTotal,
    DateTimeOffset? CollectedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    string? Error);

public interface INodeRuntimeMetricsTracker
{
    NodeRuntimeMetricsSnapshot GetSnapshot(Guid nodeId);

    IReadOnlyList<NodeRuntimeMetricsSnapshot> GetSnapshots();

    void RecordSuccess(
        Guid nodeId,
        string? modelName,
        double runningRequests,
        double waitingRequests,
        double? kvCacheUsageRatio,
        double? promptTokensTotal,
        double? generationTokensTotal,
        DateTimeOffset collectedAtUtc);

    void RecordFailure(Guid nodeId, string error, DateTimeOffset attemptedAtUtc);
}

public sealed class NullNodeRuntimeMetricsTracker : INodeRuntimeMetricsTracker
{
    public static NullNodeRuntimeMetricsTracker Instance { get; } = new();

    private NullNodeRuntimeMetricsTracker()
    {
    }

    public NodeRuntimeMetricsSnapshot GetSnapshot(Guid nodeId) =>
        new(nodeId, false, null, 0d, 0d, null, null, null, null, null, null);

    public IReadOnlyList<NodeRuntimeMetricsSnapshot> GetSnapshots() => [];

    public void RecordSuccess(
        Guid nodeId,
        string? modelName,
        double runningRequests,
        double waitingRequests,
        double? kvCacheUsageRatio,
        double? promptTokensTotal,
        double? generationTokensTotal,
        DateTimeOffset collectedAtUtc)
    {
    }

    public void RecordFailure(Guid nodeId, string error, DateTimeOffset attemptedAtUtc)
    {
    }
}
