using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Routing;

public sealed class InMemoryDeploymentPerformanceTrackerTests
{
    [Fact]
    public void Observe_builds_ewma_latency_and_failure_score()
    {
        var tracker = new InMemoryDeploymentPerformanceTracker();
        var deploymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        tracker.Observe(deploymentId, infrastructureHealthy: true, durationMilliseconds: 1000, timeToFirstByteMilliseconds: 100, now);
        tracker.Observe(deploymentId, infrastructureHealthy: false, durationMilliseconds: 1400, timeToFirstByteMilliseconds: 300, now.AddSeconds(1));
        tracker.Observe(deploymentId, infrastructureHealthy: false, durationMilliseconds: 1800, timeToFirstByteMilliseconds: 500, now.AddSeconds(2));

        var snapshot = tracker.GetSnapshot(deploymentId);

        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(237.5d, snapshot.EwmaTimeToFirstByteMilliseconds);
        Assert.Equal(1275d, snapshot.EwmaDurationMilliseconds);
        Assert.Equal(0.4375d, snapshot.InfrastructureFailureScore);
        Assert.Equal(now.AddSeconds(2), snapshot.LastObservedAtUtc);
    }

    [Fact]
    public void Healthy_samples_decay_previous_failure_signal()
    {
        var tracker = new InMemoryDeploymentPerformanceTracker();
        var deploymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        tracker.Observe(deploymentId, infrastructureHealthy: false, 500, null, now);
        tracker.Observe(deploymentId, infrastructureHealthy: true, 500, 80, now.AddSeconds(1));
        tracker.Observe(deploymentId, infrastructureHealthy: true, 500, 80, now.AddSeconds(2));

        var snapshot = tracker.GetSnapshot(deploymentId);

        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(0.5625d, snapshot.InfrastructureFailureScore);
        Assert.Equal(80d, snapshot.EwmaTimeToFirstByteMilliseconds);
    }

    [Fact]
    public void Unknown_deployment_returns_an_empty_snapshot()
    {
        var tracker = new InMemoryDeploymentPerformanceTracker();
        var deploymentId = Guid.NewGuid();

        var snapshot = tracker.GetSnapshot(deploymentId);

        Assert.Equal(deploymentId, snapshot.DeploymentId);
        Assert.Equal(0, snapshot.SampleCount);
        Assert.Null(snapshot.EwmaTimeToFirstByteMilliseconds);
        Assert.Equal(0d, snapshot.InfrastructureFailureScore);
    }
}
