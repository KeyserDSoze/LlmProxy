using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Application;

public sealed class RoutingCapacityTests
{
    [Fact]
    public async Task SelectDetailedAsync_reports_capacity_exhausted_when_node_limit_is_full()
    {
        var nodeId = Guid.NewGuid();
        var deploymentA = Guid.NewGuid();
        var deploymentB = Guid.NewGuid();
        var tracker = new InMemoryRequestLoadTracker();
        Assert.True(tracker.TryEnter(deploymentA, nodeId, deploymentMaxConcurrency: 8, nodeMaxConcurrency: 1, out var lease));

        var candidate = Candidate(deploymentB, nodeId, NodeStatus.Healthy, deploymentMaxConcurrency: 8, nodeMaxConcurrency: 1);
        var service = new RoutingService(new FakeCatalog([candidate]), new WeightedLeastLoadedRouteSelector(), tracker);

        var result = await service.SelectDetailedAsync("agic-code", null, CancellationToken.None);

        Assert.Null(result.Route);
        Assert.Equal(RoutingSelectionFailure.CapacityExhausted, result.Failure);
        lease!.Dispose();
    }

    [Fact]
    public async Task SelectDetailedAsync_reports_unavailable_when_nodes_are_unhealthy()
    {
        var candidate = Candidate(Guid.NewGuid(), Guid.NewGuid(), NodeStatus.Unhealthy, 8, 8);
        var service = new RoutingService(new FakeCatalog([candidate]), new WeightedLeastLoadedRouteSelector(), new InMemoryRequestLoadTracker());

        var result = await service.SelectDetailedAsync("agic-code", null, CancellationToken.None);

        Assert.Null(result.Route);
        Assert.Equal(RoutingSelectionFailure.Unavailable, result.Failure);
    }

    [Fact]
    public async Task SelectDetailedAsync_reports_model_not_found_for_an_unknown_name()
    {
        var service = new RoutingService(new FakeCatalog([]), new WeightedLeastLoadedRouteSelector(), new InMemoryRequestLoadTracker());

        var result = await service.SelectDetailedAsync("typo-model", null, CancellationToken.None);

        Assert.Equal(RoutingSelectionFailure.ModelNotFound, result.Failure);
    }

    [Fact]
    public async Task SelectDetailedAsync_reports_not_running_for_a_registered_model_without_a_routable_deployment()
    {
        var published = new PublicModel(Guid.NewGuid(), "agic-code", true, true, ModelSurface.OpenAi);
        var service = new RoutingService(new FakeCatalog([], [published]), new WeightedLeastLoadedRouteSelector(), new InMemoryRequestLoadTracker());

        var result = await service.SelectDetailedAsync("agic-code", null, CancellationToken.None);

        Assert.Equal(RoutingSelectionFailure.NotRunning, result.Failure);
    }

    [Fact]
    public async Task SelectDetailedAsync_keeps_unavailable_when_every_candidate_was_excluded_after_a_failed_attempt()
    {
        var candidate = Candidate(Guid.NewGuid(), Guid.NewGuid(), NodeStatus.Healthy, 8, 8);
        var published = new PublicModel(candidate.ModelId, "agic-code", true, true, ModelSurface.OpenAi);
        var service = new RoutingService(new FakeCatalog([candidate], [published]), new WeightedLeastLoadedRouteSelector(), new InMemoryRequestLoadTracker());

        var result = await service.SelectDetailedAsync("agic-code", new HashSet<Guid> { candidate.DeploymentId }, CancellationToken.None);

        Assert.Equal(RoutingSelectionFailure.Unavailable, result.Failure);
    }

    [Fact]
    public void Selector_excludes_node_when_another_deployment_consumes_physical_capacity()
    {
        var nodeId = Guid.NewGuid();
        var activeDeployment = Guid.NewGuid();
        var candidateDeployment = Guid.NewGuid();
        var tracker = new InMemoryRequestLoadTracker();
        Assert.True(tracker.TryEnter(activeDeployment, nodeId, 8, 1, out var lease));

        var selected = new WeightedLeastLoadedRouteSelector().Select(
            [Candidate(candidateDeployment, nodeId, NodeStatus.Healthy, 8, 1)],
            tracker);

        Assert.Null(selected);
        lease!.Dispose();
    }

    private static DeploymentCandidate Candidate(
        Guid deploymentId,
        Guid nodeId,
        NodeStatus status,
        int deploymentMaxConcurrency,
        int nodeMaxConcurrency) =>
        new(
            deploymentId,
            nodeId,
            "inference-01",
            "http://localhost:8000/vllm",
            Guid.NewGuid(),
            "agic-code",
            "provider-model",
            1,
            deploymentMaxConcurrency,
            status,
            nodeMaxConcurrency);

    private sealed class FakeCatalog(IReadOnlyList<DeploymentCandidate> candidates, IReadOnlyList<PublicModel>? published = null) : IDeploymentCatalog
    {
        public Task<IReadOnlyList<DeploymentCandidate>> GetCandidatesAsync(string publicModelName, CancellationToken cancellationToken)
            => Task.FromResult(candidates);

        public Task<IReadOnlyList<PublicModel>> GetPublicModelsAsync(CancellationToken cancellationToken, ModelSurface? surface = null)
            => Task.FromResult(published ?? []);
    }
}
