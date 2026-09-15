using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Application;

public sealed class NodeMaintenanceTests
{
    [Fact]
    public async Task Pending_drain_blocks_new_local_capacity_admission()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var maintenance = new LocalNodeMaintenanceCoordinator(tracker);
        var gate = new LocalRequestCapacityGate(tracker, maintenance);
        var nodeId = Guid.NewGuid();

        Assert.True(await maintenance.TryBeginDrainAsync(nodeId, TestContext.Current.CancellationToken));

        var result = await gate.TryAcquireAsync(
            Guid.NewGuid(),
            nodeId,
            deploymentMaxConcurrency: 4,
            nodeMaxConcurrency: 4,
            TestContext.Current.CancellationToken);

        Assert.False(result.Acquired);
        Assert.Equal("node_maintenance", result.RejectionScope);
    }

    [Fact]
    public async Task Drain_status_tracks_existing_request_until_lease_is_released()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var maintenance = new LocalNodeMaintenanceCoordinator(tracker);
        var gate = new LocalRequestCapacityGate(tracker, maintenance);
        var nodeId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();

        var admitted = await gate.TryAcquireAsync(
            deploymentId,
            nodeId,
            deploymentMaxConcurrency: 4,
            nodeMaxConcurrency: 4,
            TestContext.Current.CancellationToken);
        Assert.True(admitted.Acquired);
        Assert.NotNull(admitted.Lease);

        await maintenance.TryBeginDrainAsync(nodeId, TestContext.Current.CancellationToken);
        await maintenance.TryConfirmDrainAsync(nodeId, TestContext.Current.CancellationToken);

        var draining = await maintenance.GetStatusAsync(nodeId, TestContext.Current.CancellationToken);
        Assert.True(draining.AdmissionBlocked);
        Assert.Equal(1, draining.ActiveRequests);
        Assert.False(draining.Drained);

        await admitted.Lease!.DisposeAsync();

        var drained = await maintenance.GetStatusAsync(nodeId, TestContext.Current.CancellationToken);
        Assert.Equal(0, drained.ActiveRequests);
        Assert.True(drained.Drained);

        Assert.True(await maintenance.TryResumeAsync(nodeId, TestContext.Current.CancellationToken));
        var resumed = await maintenance.GetStatusAsync(nodeId, TestContext.Current.CancellationToken);
        Assert.False(resumed.AdmissionBlocked);
    }
}
