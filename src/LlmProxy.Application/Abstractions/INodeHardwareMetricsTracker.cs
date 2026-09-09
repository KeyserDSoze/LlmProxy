namespace LlmProxy.Application.Abstractions;

public sealed record NodeHardwareMetricsSnapshot(
    Guid NodeId,
    bool Available,
    int GpuCount,
    double? AverageGpuUtilizationPercent,
    double? MaxGpuUtilizationPercent,
    double? FramebufferUsedMiB,
    double? FramebufferFreeMiB,
    double? FramebufferUsageRatio,
    double? MaxTemperatureCelsius,
    double? TotalPowerUsageWatts,
    DateTimeOffset? CollectedAtUtc,
    DateTimeOffset? LastAttemptAtUtc,
    string? Error);

public interface INodeHardwareMetricsTracker
{
    NodeHardwareMetricsSnapshot GetSnapshot(Guid nodeId);

    IReadOnlyList<NodeHardwareMetricsSnapshot> GetSnapshots();

    void RecordSuccess(
        Guid nodeId,
        int gpuCount,
        double? averageGpuUtilizationPercent,
        double? maxGpuUtilizationPercent,
        double? framebufferUsedMiB,
        double? framebufferFreeMiB,
        double? framebufferUsageRatio,
        double? maxTemperatureCelsius,
        double? totalPowerUsageWatts,
        DateTimeOffset collectedAtUtc);

    void RecordFailure(Guid nodeId, string error, DateTimeOffset attemptedAtUtc);

    void Remove(Guid nodeId);
}
