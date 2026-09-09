using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Infrastructure.Telemetry;

public sealed class NodeHardwareMetricsTracker : INodeHardwareMetricsTracker
{
    private readonly ConcurrentDictionary<Guid, NodeHardwareMetricsSnapshot> _snapshots = new();

    public NodeHardwareMetricsSnapshot GetSnapshot(Guid nodeId) =>
        _snapshots.TryGetValue(nodeId, out var snapshot)
            ? snapshot
            : Empty(nodeId);

    public IReadOnlyList<NodeHardwareMetricsSnapshot> GetSnapshots() =>
        _snapshots.Values
            .OrderByDescending(snapshot => snapshot.LastAttemptAtUtc)
            .ThenBy(snapshot => snapshot.NodeId)
            .ToArray();

    public void RecordSuccess(
        Guid nodeId,
        int gpuCount,
        double? averageGpuUtilizationPercent,
        double? maxGpuUtilizationPercent,
        double? framebufferUsedMiB,
        double? framebufferFreeMiB,
        double? framebufferUsageRatio,
        double? maxTemperatureCelsius,
        double? totalPowerUsageWatts,
        DateTimeOffset collectedAtUtc)
    {
        ValidateNodeId(nodeId);
        ArgumentOutOfRangeException.ThrowIfNegative(gpuCount);

        ValidateNonNegative(averageGpuUtilizationPercent, nameof(averageGpuUtilizationPercent));
        ValidateNonNegative(maxGpuUtilizationPercent, nameof(maxGpuUtilizationPercent));
        ValidateNonNegative(framebufferUsedMiB, nameof(framebufferUsedMiB));
        ValidateNonNegative(framebufferFreeMiB, nameof(framebufferFreeMiB));
        ValidateNonNegative(totalPowerUsageWatts, nameof(totalPowerUsageWatts));

        if (framebufferUsageRatio is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(framebufferUsageRatio));
        }

        _snapshots[nodeId] = new NodeHardwareMetricsSnapshot(
            nodeId,
            true,
            gpuCount,
            averageGpuUtilizationPercent,
            maxGpuUtilizationPercent,
            framebufferUsedMiB,
            framebufferFreeMiB,
            framebufferUsageRatio,
            maxTemperatureCelsius,
            totalPowerUsageWatts,
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

    private static NodeHardwareMetricsSnapshot Empty(Guid nodeId) =>
        new(nodeId, false, 0, null, null, null, null, null, null, null, null, null, null);

    private static void ValidateNodeId(Guid nodeId)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Node id is required.", nameof(nodeId));
        }
    }

    private static void ValidateNonNegative(double? value, string name)
    {
        if (value is < 0d || value is double numeric && !double.IsFinite(numeric))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}
