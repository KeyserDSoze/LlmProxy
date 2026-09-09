using LlmProxy.Infrastructure.Telemetry;

namespace LlmProxy.UnitTests.Telemetry;

public sealed class NodeHardwareMetricsTrackerTests
{
    [Fact]
    public void RecordSuccess_publishes_latest_hardware_snapshot()
    {
        var tracker = new NodeHardwareMetricsTracker();
        var nodeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        tracker.RecordSuccess(nodeId, 2, 60, 80, 4000, 12000, 0.25, 67, 261, now);

        var snapshot = tracker.GetSnapshot(nodeId);
        Assert.True(snapshot.Available);
        Assert.Equal(2, snapshot.GpuCount);
        Assert.Equal(60d, snapshot.AverageGpuUtilizationPercent);
        Assert.Equal(80d, snapshot.MaxGpuUtilizationPercent);
        Assert.Equal(0.25d, snapshot.FramebufferUsageRatio);
        Assert.Equal(now, snapshot.CollectedAtUtc);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public void RecordFailure_marks_snapshot_unavailable_but_preserves_last_success_values()
    {
        var tracker = new NodeHardwareMetricsTracker();
        var nodeId = Guid.NewGuid();
        var collected = DateTimeOffset.UtcNow.AddSeconds(-5);
        var failed = DateTimeOffset.UtcNow;

        tracker.RecordSuccess(nodeId, 1, 42, 42, 2000, 6000, 0.25, 58, 95, collected);
        tracker.RecordFailure(nodeId, "HTTP 503", failed);

        var snapshot = tracker.GetSnapshot(nodeId);
        Assert.False(snapshot.Available);
        Assert.Equal(42d, snapshot.AverageGpuUtilizationPercent);
        Assert.Equal(collected, snapshot.CollectedAtUtc);
        Assert.Equal(failed, snapshot.LastAttemptAtUtc);
        Assert.Equal("HTTP 503", snapshot.Error);
    }
}
