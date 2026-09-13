using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class InMemoryRequestLoadTrackerTests
{
    [Fact]
    public void TryEnter_enforces_aggregate_node_capacity_across_deployments()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var nodeId = Guid.NewGuid();
        var deploymentA = Guid.NewGuid();
        var deploymentB = Guid.NewGuid();
        var deploymentC = Guid.NewGuid();

        Assert.True(tracker.TryEnter(deploymentA, nodeId, 8, 2, out var leaseA));
        Assert.True(tracker.TryEnter(deploymentB, nodeId, 8, 2, out var leaseB));
        Assert.False(tracker.TryEnter(deploymentC, nodeId, 8, 2, out var rejectedLease));
        Assert.Null(rejectedLease);
        Assert.Equal(2, tracker.GetNodeActive(nodeId));

        leaseA!.Dispose();

        Assert.True(tracker.TryEnter(deploymentC, nodeId, 8, 2, out var leaseC));
        Assert.Equal(2, tracker.GetNodeActive(nodeId));

        leaseB!.Dispose();
        leaseC!.Dispose();
        Assert.Equal(0, tracker.GetNodeActive(nodeId));
    }

    [Fact]
    public void TryEnter_enforces_deployment_capacity_before_node_is_full()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var nodeId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();

        Assert.True(tracker.TryEnter(deploymentId, nodeId, 1, 10, out var lease));
        Assert.False(tracker.TryEnter(deploymentId, nodeId, 1, 10, out var rejectedLease));
        Assert.Null(rejectedLease);
        Assert.Equal(1, tracker.GetActive(deploymentId));
        Assert.Equal(1, tracker.GetNodeActive(nodeId));

        lease!.Dispose();
        Assert.Equal(0, tracker.GetActive(deploymentId));
        Assert.Equal(0, tracker.GetNodeActive(nodeId));
    }

    [Fact]
    public void Lease_disposal_is_idempotent()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var nodeId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        Assert.True(tracker.TryEnter(deploymentId, nodeId, 2, 2, out var lease));

        lease!.Dispose();
        lease.Dispose();

        Assert.Equal(0, tracker.GetActive(deploymentId));
        Assert.Equal(0, tracker.GetNodeActive(nodeId));
    }
}
