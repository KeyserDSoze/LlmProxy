using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Routing;

public sealed class WeightedLeastLoadedRouteSelectorTests
{
    [Fact]
    public void Select_prefers_less_loaded_candidate()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate(firstId, "dgx-01", maxConcurrency: 4, weight: 1),
            Candidate(secondId, "dgx-02", maxConcurrency: 4, weight: 1)
        };
        var tracker = new StubLoadTracker(new Dictionary<Guid, int>
        {
            [firstId] = 3,
            [secondId] = 0
        });

        var route = new WeightedLeastLoadedRouteSelector().Select(candidates, tracker);

        Assert.NotNull(route);
        Assert.Equal(secondId, route.DeploymentId);
    }

    [Fact]
    public void Select_respects_weight_when_capacity_is_available()
    {
        var lowWeightId = Guid.NewGuid();
        var highWeightId = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate(lowWeightId, "dgx-01", maxConcurrency: 4, weight: 1),
            Candidate(highWeightId, "dgx-02", maxConcurrency: 4, weight: 4)
        };
        var tracker = new StubLoadTracker();

        var route = new WeightedLeastLoadedRouteSelector().Select(candidates, tracker);

        Assert.NotNull(route);
        Assert.Equal(highWeightId, route.DeploymentId);
    }

    [Fact]
    public void Select_avoids_a_high_weight_backend_with_recent_infrastructure_failures()
    {
        var stableId = Guid.NewGuid();
        var failingId = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate(stableId, "dgx-stable", maxConcurrency: 4, weight: 1),
            Candidate(failingId, "dgx-fast-but-failing", maxConcurrency: 4, weight: 4)
        };
        var performance = new InMemoryDeploymentPerformanceTracker();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 3; index++)
        {
            performance.Observe(failingId, infrastructureHealthy: false, 900, 100, now.AddSeconds(index));
        }
        for (var index = 0; index < 3; index++)
        {
            performance.Observe(stableId, infrastructureHealthy: true, 1000, 150, now.AddSeconds(index));
        }

        var route = new WeightedLeastLoadedRouteSelector(performance)
            .Select(candidates, new StubLoadTracker());

        Assert.NotNull(route);
        Assert.Equal(stableId, route.DeploymentId);
    }

    [Fact]
    public void Select_uses_health_penalty_before_tie_breaking()
    {
        var degradedId = Guid.NewGuid();
        var healthyId = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate(degradedId, "a-degraded", status: NodeStatus.Degraded),
            Candidate(healthyId, "z-healthy", status: NodeStatus.Healthy)
        };

        var route = new WeightedLeastLoadedRouteSelector()
            .Select(candidates, new StubLoadTracker());

        Assert.NotNull(route);
        Assert.Equal(healthyId, route.DeploymentId);
    }

    [Fact]
    public void Select_skips_unhealthy_draining_and_full_candidates()
    {
        var unhealthy = Guid.NewGuid();
        var draining = Guid.NewGuid();
        var full = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate(unhealthy, "dgx-01", status: NodeStatus.Unhealthy),
            Candidate(draining, "dgx-02", status: NodeStatus.Draining),
            Candidate(full, "dgx-03", maxConcurrency: 1),
            Candidate(healthy, "dgx-04", status: NodeStatus.Healthy)
        };
        var tracker = new StubLoadTracker(new Dictionary<Guid, int> { [full] = 1 });

        var route = new WeightedLeastLoadedRouteSelector().Select(candidates, tracker);

        Assert.NotNull(route);
        Assert.Equal(healthy, route.DeploymentId);
    }

    [Fact]
    public void Select_returns_null_when_every_candidate_is_unavailable()
    {
        var id = Guid.NewGuid();
        var candidates = new[] { Candidate(id, "dgx-01", status: NodeStatus.Unhealthy) };

        var route = new WeightedLeastLoadedRouteSelector().Select(candidates, new StubLoadTracker());

        Assert.Null(route);
    }

    private static DeploymentCandidate Candidate(
        Guid deploymentId,
        string nodeName,
        int maxConcurrency = 4,
        int weight = 1,
        NodeStatus status = NodeStatus.Healthy)
        => new(
            deploymentId,
            Guid.NewGuid(),
            nodeName,
            $"http://{nodeName}:8000",
            Guid.NewGuid(),
            "agic-code-fast",
            "provider-model",
            weight,
            maxConcurrency,
            status);

    private sealed class StubLoadTracker(IReadOnlyDictionary<Guid, int>? values = null) : IRequestLoadTracker
    {
        public int GetActive(Guid deploymentId)
            => values is not null && values.TryGetValue(deploymentId, out var value) ? value : 0;

        public IDisposable Enter(Guid deploymentId) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
