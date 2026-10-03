using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.UnitTests.Application;

public sealed class RoutingServiceTests
{
    [Fact]
    public async Task SelectAsync_passes_catalog_candidates_to_selector()
    {
        var candidate = Candidate(Guid.NewGuid());
        var catalog = new FakeCatalog([candidate]);
        var selector = new RecordingSelector(new RouteSelection(
            candidate.DeploymentId,
            candidate.NodeId,
            candidate.NodeName,
            candidate.BaseAddress,
            candidate.PublicModelName,
            candidate.ProviderModelName,
            candidate.MaxConcurrency));
        var tracker = new EmptyLoadTracker();
        var service = new RoutingService(catalog, selector, tracker);

        var selected = await service.SelectAsync("agic-code-fast", CancellationToken.None);

        Assert.NotNull(selected);
        Assert.Equal("agic-code-fast", catalog.RequestedModel);
        Assert.Single(selector.Candidates);
        Assert.Equal(candidate.DeploymentId, selector.Candidates[0].DeploymentId);
        Assert.Same(tracker, selector.LoadTracker);
    }

    [Fact]
    public async Task SelectAsync_filters_excluded_deployments_before_selector()
    {
        var excluded = Candidate(Guid.NewGuid());
        var available = Candidate(Guid.NewGuid());
        var catalog = new FakeCatalog([excluded, available]);
        var selector = new RecordingSelector(null);
        var service = new RoutingService(catalog, selector, new EmptyLoadTracker());

        await service.SelectAsync(
            "agic-code-fast",
            new HashSet<Guid> { excluded.DeploymentId },
            CancellationToken.None);

        Assert.Single(selector.Candidates);
        Assert.Equal(available.DeploymentId, selector.Candidates[0].DeploymentId);
    }

    private static DeploymentCandidate Candidate(Guid deploymentId)
        => new(
            deploymentId,
            Guid.NewGuid(),
            "inference-test",
            "http://inference-test:8000",
            Guid.NewGuid(),
            "agic-code-fast",
            "provider-model",
            1,
            4,
            NodeStatus.Healthy);

    private sealed class FakeCatalog(IReadOnlyList<DeploymentCandidate> candidates) : IDeploymentCatalog
    {
        public string? RequestedModel { get; private set; }

        public Task<IReadOnlyList<DeploymentCandidate>> GetCandidatesAsync(string publicModelName, CancellationToken cancellationToken)
        {
            RequestedModel = publicModelName;
            return Task.FromResult(candidates);
        }

        public Task<IReadOnlyList<PublicModel>> GetPublicModelsAsync(CancellationToken cancellationToken, ModelSurface? surface = null)
            => Task.FromResult<IReadOnlyList<PublicModel>>([]);
    }

    private sealed class RecordingSelector(RouteSelection? result) : IRouteSelector
    {
        public IReadOnlyList<DeploymentCandidate> Candidates { get; private set; } = [];
        public IRequestLoadTracker? LoadTracker { get; private set; }

        public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
        {
            Candidates = candidates;
            LoadTracker = loadTracker;
            return result;
        }
    }

    private sealed class EmptyLoadTracker : IRequestLoadTracker
    {
        public int GetActive(Guid deploymentId) => 0;
        public IDisposable Enter(Guid deploymentId) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
