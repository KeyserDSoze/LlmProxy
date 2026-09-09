using LlmProxy.Application.Routing;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Routing;

public sealed class RoundRobinRouteSelectorTests
{
    [Fact]
    public void Select_rotates_between_available_nodes()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var candidates = new[]
        {
            Candidate("a", 1),
            Candidate("b", 1)
        };
        var selector = new RoundRobinRouteSelector();

        var selections = Enumerable.Range(0, 4)
            .Select(_ => selector.Select(candidates, tracker)!.NodeName)
            .ToArray();

        Assert.Equal(new[] { "a", "b", "a", "b" }, selections);
    }

    [Fact]
    public void Select_skips_a_saturated_node()
    {
        var tracker = new InMemoryRequestLoadTracker();
        var saturated = Candidate("a", 1, maxConcurrency: 1);
        var available = Candidate("b", 1, maxConcurrency: 1);
        using var lease = tracker.Enter(saturated.DeploymentId);

        var selected = new RoundRobinRouteSelector().Select([saturated, available], tracker);

        Assert.Equal("b", selected!.NodeName);
    }

    private static DeploymentCandidate Candidate(string nodeName, int weight, int maxConcurrency = 4) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            nodeName,
            $"http://localhost:3450/{nodeName}",
            Guid.NewGuid(),
            "agic-code-fast",
            "provider-model",
            weight,
            maxConcurrency,
            NodeStatus.Healthy);
}
