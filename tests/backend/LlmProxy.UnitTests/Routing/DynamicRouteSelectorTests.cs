using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;

namespace LlmProxy.UnitTests.Routing;

public sealed class DynamicRouteSelectorTests
{
    [Fact]
    public void Strategy_can_change_without_recreating_the_selector()
    {
        var state = new RoutingStrategyState(RoutingStrategy.RoundRobin);
        var selector = new DynamicRouteSelector(state);
        var tracker = new FakeLoadTracker();
        var candidates = new[]
        {
            Candidate("a", 1),
            Candidate("b", 3)
        };

        var first = selector.Select(candidates, tracker);
        var second = selector.Select(candidates, tracker);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.NodeId, second.NodeId);

        state.Set(RoutingStrategy.WeightedRoundRobin);
        var weighted = Enumerable.Range(0, 4)
            .Select(_ => selector.Select(candidates, tracker)!.NodeName)
            .ToArray();

        Assert.Equal(1, weighted.Count(name => name == "a"));
        Assert.Equal(3, weighted.Count(name => name == "b"));
    }

    [Fact]
    public void Strategy_state_is_immediately_visible()
    {
        var state = new RoutingStrategyState(RoutingStrategy.WeightedLeastLoaded);
        state.Set(RoutingStrategy.RoundRobin);
        Assert.Equal(RoutingStrategy.RoundRobin, state.Current);
    }

    private static DeploymentCandidate Candidate(string name, int weight)
    {
        var nodeId = Guid.NewGuid();
        return new DeploymentCandidate(
            Guid.NewGuid(),
            nodeId,
            name,
            $"http://{name}:8000",
            Guid.NewGuid(),
            "agic-code-fast",
            "provider-model",
            weight,
            8,
            NodeStatus.Healthy);
    }

    private sealed class FakeLoadTracker : IRequestLoadTracker
    {
        public int GetActive(Guid deploymentId) => 0;
        public IDisposable Enter(Guid deploymentId) => new NoopLease();
        private sealed class NoopLease : IDisposable { public void Dispose() { } }
    }
}
