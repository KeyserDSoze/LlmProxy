using LlmProxy.Infrastructure.Telemetry;

namespace LlmProxy.UnitTests.Telemetry;

public sealed class VllmRuntimeMetricsTrackerTests
{
    [Fact]
    public void Successful_collection_is_exposed_to_routing_and_admin_readers()
    {
        var tracker = new VllmRuntimeMetricsTracker();
        var nodeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        tracker.RecordSuccess(nodeId, "Qwen/Test", 2, 3, 0.76, 1000, 500, now);

        var snapshot = tracker.GetSnapshot(nodeId);
        Assert.True(snapshot.Available);
        Assert.Equal("Qwen/Test", snapshot.ModelName);
        Assert.Equal(2d, snapshot.RunningRequests);
        Assert.Equal(3d, snapshot.WaitingRequests);
        Assert.Equal(0.76d, snapshot.KvCacheUsageRatio);
        Assert.Equal(now, snapshot.CollectedAtUtc);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public void Collection_failure_marks_snapshot_unavailable_but_retains_last_good_values()
    {
        var tracker = new VllmRuntimeMetricsTracker();
        var nodeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        tracker.RecordSuccess(nodeId, "Qwen/Test", 1, 0, 0.25, 100, 50, now);
        tracker.RecordFailure(nodeId, "HTTP 503", now.AddSeconds(5));

        var snapshot = tracker.GetSnapshot(nodeId);
        Assert.False(snapshot.Available);
        Assert.Equal(1d, snapshot.RunningRequests);
        Assert.Equal(0.25d, snapshot.KvCacheUsageRatio);
        Assert.Equal(now, snapshot.CollectedAtUtc);
        Assert.Equal(now.AddSeconds(5), snapshot.LastAttemptAtUtc);
        Assert.Equal("HTTP 503", snapshot.Error);
    }
}
