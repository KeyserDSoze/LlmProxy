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
            [new RouteNodeSnapshot(nodeId, "inference-01", "http://inference-01:8000/root", true, NodeStatus.Healthy, 2, 12)],
            [new RouteModelSnapshot(modelId, "agic-code", "provider-code", true, true, true)],
            [new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, true, 3, 5)]);

        var candidates = await catalog.GetCandidatesAsync("agic-code", CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(deploymentId, candidate.DeploymentId);
        Assert.Equal("http://inference-01:8000/root", candidate.BaseAddress);
        Assert.Equal("provider-code", candidate.ProviderModelName);
        Assert.Equal(6, candidate.Weight);
        Assert.Equal(5, candidate.MaxConcurrency);
        Assert.Equal(12, candidate.NodeMaxConcurrency);
        Assert.Equal(NodeStatus.Healthy, candidate.NodeStatus);
    }

    [Fact]
    public async Task Candidate_pools_are_partitioned_by_requested_logical_model()
    {
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var nodeC = Guid.NewGuid();
        var nodeD = Guid.NewGuid();
        var nodeE = Guid.NewGuid();
        var model1 = Guid.NewGuid();
        var model2 = Guid.NewGuid();
        var catalog = new InMemoryRouteCatalog();

        catalog.Replace(
            [
                new RouteNodeSnapshot(nodeA, "inference-a", "http://inference-a:8000", true, NodeStatus.Healthy, 1, 8),
                new RouteNodeSnapshot(nodeB, "inference-b", "http://inference-b:8000", true, NodeStatus.Healthy, 1, 8),
                new RouteNodeSnapshot(nodeC, "inference-c", "http://inference-c:8000", true, NodeStatus.Healthy, 1, 8),
                new RouteNodeSnapshot(nodeD, "inference-d", "http://inference-d:8000", true, NodeStatus.Healthy, 1, 8),
                new RouteNodeSnapshot(nodeE, "inference-e", "http://inference-e:8000", true, NodeStatus.Healthy, 1, 8)
            ],
            [
                new RouteModelSnapshot(model1, "model-1", "provider-model-1", true, true, true),
                new RouteModelSnapshot(model2, "model-2", "provider-model-2", true, true, true)
            ],
            [
                new RouteDeploymentSnapshot(Guid.NewGuid(), nodeA, model1, true, 1, null),
                new RouteDeploymentSnapshot(Guid.NewGuid(), nodeB, model1, true, 1, null),
                new RouteDeploymentSnapshot(Guid.NewGuid(), nodeB, model2, true, 1, null),
                new RouteDeploymentSnapshot(Guid.NewGuid(), nodeC, model2, true, 1, null),
                new RouteDeploymentSnapshot(Guid.NewGuid(), nodeD, model2, true, 1, null),
                new RouteDeploymentSnapshot(Guid.NewGuid(), nodeE, model2, true, 1, null)
            ]);

        var model1Candidates = await catalog.GetCandidatesAsync("model-1", CancellationToken.None);
        var model2Candidates = await catalog.GetCandidatesAsync("model-2", CancellationToken.None);

        Assert.Equal([nodeA, nodeB], model1Candidates.Select(candidate => candidate.NodeId).Order().ToArray());
        Assert.Equal([nodeB, nodeC, nodeD, nodeE], model2Candidates.Select(candidate => candidate.NodeId).Order().ToArray());
        Assert.All(model1Candidates, candidate => Assert.Equal("provider-model-1", candidate.ProviderModelName));
        Assert.All(model2Candidates, candidate => Assert.Equal("provider-model-2", candidate.ProviderModelName));
    }

    [Fact]
    public async Task Deployment_runtime_address_overrides_the_physical_node_address()
    {
        var nodeId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var catalog = new InMemoryRouteCatalog();

        catalog.Replace(
            [new RouteNodeSnapshot(nodeId, "hardware-01", "http://hardware-01:8000", true, NodeStatus.Healthy, 1, 8)],
            [new RouteModelSnapshot(modelId, "model-a", "provider-a", true, true, true)],
            [new RouteDeploymentSnapshot(Guid.NewGuid(), nodeId, modelId, true, 1, 4, "http://hardware-01:18001/model-a")]);

        var candidate = Assert.Single(await catalog.GetCandidatesAsync("model-a", CancellationToken.None));

        Assert.Equal("http://hardware-01:18001/model-a", candidate.BaseAddress);
    }

    [Fact]
    public async Task Uses_node_capacity_when_deployment_override_is_absent()
    {
        var nodeId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var catalog = new InMemoryRouteCatalog();
        catalog.Replace(
            [new RouteNodeSnapshot(nodeId, "inference-01", "http://inference-01:8000", true, NodeStatus.Healthy, 1, 9)],
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
            [new RouteNodeSnapshot(nodeId, "inference-01", "http://inference-01:8000", true, NodeStatus.Healthy, 1, 4)],
            [new RouteModelSnapshot(modelId, "agic-code", "provider-code", true, true, true)],
            [new RouteDeploymentSnapshot(Guid.NewGuid(), nodeId, modelId, true, 1, null)]);

        catalog.Upsert(new RouteNodeSnapshot(
            nodeId,
            "inference-01",
            "http://inference-01:8000/new-root",
            true,
            NodeStatus.Draining,
            4,
            7));

        var candidate = Assert.Single(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));

        Assert.Equal("http://inference-01:8000/new-root", candidate.BaseAddress);
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
            [new RouteNodeSnapshot(nodeId, "inference-01", "http://inference-01:8000", true, NodeStatus.Healthy, 1, 4)],
            [new RouteModelSnapshot(modelId, "agic-code", "provider-code", true, true, true)],
            [new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, true, 1, null)]);

        catalog.Upsert(new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, false, 1, null));
        Assert.Empty(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));

        catalog.Upsert(new RouteDeploymentSnapshot(deploymentId, nodeId, modelId, true, 1, null));
        catalog.Upsert(new RouteNodeSnapshot(nodeId, "inference-01", "http://inference-01:8000", false, NodeStatus.Disabled, 1, 4));
        Assert.Empty(await catalog.GetCandidatesAsync("agic-code", CancellationToken.None));

        catalog.Upsert(new RouteNodeSnapshot(nodeId, "inference-01", "http://inference-01:8000", true, NodeStatus.Healthy, 1, 4));
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
