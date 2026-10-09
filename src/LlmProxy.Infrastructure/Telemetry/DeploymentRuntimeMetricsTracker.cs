using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class DeploymentRuntimeMetricsTracker : IDeploymentRuntimeMetricsTracker
{
    private readonly ConcurrentDictionary<Guid, DeploymentRuntimeMetricsSnapshot> _snapshots = new();

    public DeploymentRuntimeMetricsSnapshot GetSnapshot(Guid deploymentId) =>
        _snapshots.TryGetValue(deploymentId, out var snapshot)
            ? snapshot
            : Empty(deploymentId, Guid.Empty);

    public IReadOnlyList<DeploymentRuntimeMetricsSnapshot> GetSnapshots() =>
        _snapshots.Values.OrderByDescending(x => x.LastAttemptAtUtc).ToArray();

    public void RecordSuccess(Guid deploymentId, Guid nodeId, string runtime, string? modelName,
        double running, double waiting, double? cacheUsage,
        double? promptTokens, double? generatedTokens, DateTimeOffset collectedAtUtc)
    {
        ValidateIds(deploymentId, nodeId);
        if (!double.IsFinite(running) || !double.IsFinite(waiting) || running < 0 || waiting < 0)
            throw new ArgumentOutOfRangeException(nameof(running));
        _snapshots[deploymentId] = new(
            deploymentId, nodeId, true, runtime, modelName, running, waiting,
            cacheUsage, promptTokens, generatedTokens, collectedAtUtc, collectedAtUtc, null);
    }

    public void RecordFailure(Guid deploymentId, Guid nodeId, string error, DateTimeOffset attemptedAtUtc)
    {
        ValidateIds(deploymentId, nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        _snapshots.AddOrUpdate(deploymentId,
            _ => Empty(deploymentId, nodeId) with { LastAttemptAtUtc = attemptedAtUtc, Error = error },
            (_, old) => old with { Available = false, LastAttemptAtUtc = attemptedAtUtc, Error = error });
    }

    public void RetainOnly(IReadOnlySet<Guid> currentDeploymentIds)
    {
        foreach (var id in _snapshots.Keys)
            if (!currentDeploymentIds.Contains(id)) _snapshots.TryRemove(id, out _);
    }

    private static DeploymentRuntimeMetricsSnapshot Empty(Guid deploymentId, Guid nodeId) =>
        new(deploymentId, nodeId, false, null, null, 0, 0, null, null, null, null, null, null);

    private static void ValidateIds(Guid deploymentId, Guid nodeId)
    {
        if (deploymentId == Guid.Empty || nodeId == Guid.Empty)
            throw new ArgumentException("Both deployment and node IDs are required.");
    }
}
