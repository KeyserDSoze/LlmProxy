using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class VllmRuntimeMetricsTracker : INodeRuntimeMetricsTracker
{
    private readonly ConcurrentDictionary<Guid, NodeRuntimeMetricsSnapshot> _snapshots = new();

    public NodeRuntimeMetricsSnapshot GetSnapshot(Guid nodeId) =>
        _snapshots.TryGetValue(nodeId, out var snapshot)
            ? snapshot
            : Empty(nodeId);

    public IReadOnlyList<NodeRuntimeMetricsSnapshot> GetSnapshots() =>
        _snapshots.Values
            .OrderByDescending(snapshot => snapshot.LastAttemptAtUtc)
            .ThenBy(snapshot => snapshot.NodeId)
            .ToArray();

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
        ValidateNodeId(nodeId);
        ArgumentOutOfRangeException.ThrowIfNegative(runningRequests);
        ArgumentOutOfRangeException.ThrowIfNegative(waitingRequests);

        if (kvCacheUsageRatio is < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(kvCacheUsageRatio));
        }

        _snapshots[nodeId] = new NodeRuntimeMetricsSnapshot(
            nodeId,
            true,
            modelName,
            runningRequests,
            waitingRequests,
            kvCacheUsageRatio,
            promptTokensTotal,
            generationTokensTotal,
            collectedAtUtc,
            collectedAtUtc,
            null);
    }

    public void RecordFailure(Guid nodeId, string error, DateTimeOffset attemptedAtUtc)
    {
        ValidateNodeId(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        _snapshots.AddOrUpdate(
            nodeId,
            _ => Empty(nodeId) with
            {
                LastAttemptAtUtc = attemptedAtUtc,
                Error = error
            },
            (_, current) => current with
            {
                Available = false,
                LastAttemptAtUtc = attemptedAtUtc,
                Error = error
            });
    }

    private static NodeRuntimeMetricsSnapshot Empty(Guid nodeId) =>
        new(nodeId, false, null, 0d, 0d, null, null, null, null, null, null);

    private static void ValidateNodeId(Guid nodeId)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Node id is required.", nameof(nodeId));
        }
    }
}
