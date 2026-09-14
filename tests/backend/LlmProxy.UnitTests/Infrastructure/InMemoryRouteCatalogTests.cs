using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Routing;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class InMemoryRouteCatalogTests
{
    [Fact]
    public async Task Resolves_enabled_graph_without_database_access()
    {
        var nodeId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var catalog = new InMemoryRouteCatalog();

        catalog.Replace(
            [new RouteNodeSnapshot(nodeId, "dgx-01", "http://dgx-01:8000/root", true, NodeStatus.Healthy, 2, 12)],
            [new RouteModelSnapshot(modelId, "agic-code", "provider-code", true, true, true)],
            [new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, true, 3, 5)]);

        var candidates = await catalog.GetCandidatesAsync("agic-code", CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(deploymentId, candidate.DeploymentId);
        Assert.Equal("http://dgx-01:8000/root", candidate.BaseAddress);
        Assert.Equal("provider-code", candidate.ProviderModelName);
        Assert.Equal(6, candidate.Weight);
        Assert.Equal(5, candidate.MaxConcurrency);
        Assert.Equal(12, candidate.NodeMaxConcurrency);
        Assert.Equal(NodeStatus.Healthy, candidate.NodeStatus);
    }

    [Fact]
    public async Task Uses_node_capacity_when_deployment_override_is_absent()
    {
        var nodeId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var catalog = new InMemoryRouteCatalog();
        catalog.Replace(
            [new RouteNodeSnapshot(nodeId, "dgx-01", "http://dgx-01:8000", true, NodeStatus.Healthy, 1, 9)],
            [new RouteModelSnapshot(modelId, "agic-code", "provider-code", true, true, true)],
            [new RouteDeploymentSnapshot(Guid.NewGuid(), nodeId, modelId, true, 1, null)]);

        var candidate = Assert.Single(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));

        Assert.Equal(9, candidate.MaxConcurrency);
        Assert.Equal(9, candidate.NodeMaxConcurrency);
    }

    [Fact]
    public async Task Live_node_update_is_visible_to_next_lookup()
    {
        var nodeId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var catalog = new InMemoryRouteCatalog();
        catalog.Replace(
            [new RouteNodeSnapshot(nodeId, "dgx-01", "http://dgx-01:8000", true, NodeStatus.Healthy, 1, 4)],
            [new RouteModelSnapshot(modelId, "agic-code", "provider-code", true, true, true)],
            [new RouteDeploymentSnapshot(Guid.NewGuid(), nodeId, modelId, true, 1, null)]);

        catalog.Upsert(new RouteNodeSnapshot(
            nodeId,
            "dgx-01",
            "http://dgx-01:8000/new-root",
            true,
            NodeStatus.Draining,
            4,
            7));

        var candidate = Assert.Single(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));

        Assert.Equal("http://dgx-01:8000/new-root", candidate.BaseAddress);
        Assert.Equal(NodeStatus.Draining, candidate.NodeStatus);
        Assert.Equal(4, candidate.Weight);
        Assert.Equal(7, candidate.MaxConcurrency);
        Assert.True(catalog.GetStatus().Version >= 2);
    }

    [Fact]
    public async Task Disabled_model_node_or_deployment_is_not_exposed()
    {
        var nodeId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var catalog = new InMemoryRouteCatalog();
        catalog.Replace(
            [new RouteNodeSnapshot(nodeId, "dgx-01", "http://dgx-01:8000", true, NodeStatus.Healthy, 1, 4)],
            [new RouteModelSnapshot(modelId, "agic-code", "provider-code", true, true, true)],
            [new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, true, 1, null)]);

        catalog.Upsert(new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, false, 1, null));
        Assert.Empty(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));

        catalog.Upsert(new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, true, 1, null));
        catalog.Upsert(new RouteNodeSnapshot(nodeId, "dgx-01", "http://dgx-01:8000", false, NodeStatus.Disabled, 1, 4));
        Assert.Empty(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));

        catalog.Upsert(new RouteNodeSnapshot(nodeId, "dgx-01", "http://dgx-01:8000", true, NodeStatus.Healthy, 1, 4));
        catalog.Upsert(new RouteModelSnapshot(modelId, "agic-code", "provider-code", false, true, true));
        Assert.Empty(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));
    }

    [Fact]
    public async Task Public_models_are_filtered_and_sorted_from_snapshot()
    {
        var catalog = new InMemoryRouteCatalog();
        catalog.Replace(
            [],
            [
                new RouteModelSnapshot(Guid.NewGuid(), "z-model", "z-provider", true, true, false),
                new RouteModelSnapshot(Guid.NewGuid(), "a-model", "a-provider", true, false, true),
                new RouteModelSnapshot(Guid.NewGuid(), "hidden", "hidden-provider", false, true, true)
            ],
            []);

        var models = await catalog.GetPublicModelsAsync(CancellationToken.None);

        Assert.Equal(2, models.Count);
        Assert.Equal("a-model", models[0].PublicName);
        Assert.Equal("z-model", models[1].PublicName);
    }
}
