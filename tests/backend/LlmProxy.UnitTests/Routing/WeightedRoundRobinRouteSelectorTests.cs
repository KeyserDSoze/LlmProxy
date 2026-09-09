using LlmProxy.Application.Routing;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Routing;

public sealed class WeightedRoundRobinRouteSelectorTests
{
    [Fact]
    public void Select_respects_weight_for_sequential_requests()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var candidates = new[]
        {
            Candidate("primary", 1),
            Candidate("alternate", 3)
        };
        var selector = new WeightedRoundRobinRouteSelector();

        var selections = Enumerable.Range(0, 8)
            .Select(_ => selector.Select(candidates, tracker)!.NodeName)
            .ToArray();

        Assert.Equal(2, selections.Count(name => name == "primary"));
        Assert.Equal(6, selections.Count(name => name == "alternate"));
    }

    [Fact]
    public void Select_never_routes_to_unhealthy_nodes_even_with_high_weight()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var healthy = Candidate("healthy", 1);
        var unhealthy = Candidate("unhealthy", 100) with { NodeStatus = NodeStatus.Unhealthy };

        var selected = new WeightedRoundRobinRouteSelector().Select([healthy, unhealthy], tracker);

        Assert.Equal("healthy", selected!.NodeName);
    }

    private static DeploymentCandidate Candidate(string nodeName, int weight) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            nodeName,
            $"http://localhost:3450/{nodeName}",
            Guid.NewGuid(),
            "agic-code-fast",
            "provider-model",
            weight,
            4,
            NodeStatus.Healthy);
}
