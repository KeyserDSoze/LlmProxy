using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;

namespace LlmProxy.Infrastructure.Routing;

public sealed class InMemoryRouteCatalog : IRouteCatalog
{
    private sealed record CatalogState(
        long Version,
        IReadOnlyDictionary<Guid, RouteNodeSnapshot> Nodes,
        IReadOnlyDictionary<Guid, RouteModelSnapshot> Models,
        IReadOnlyDictionary<Guid, RouteDeploymentSnapshot> Deployments);

    private readonly object _gate = new();
    private CatalogState _state = Empty();

    public Task<IReadOnlyList<DeploymentCandidate>> GetCandidatesAsync(
        string publicModelName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Volatile.Read(ref _state);

        var modelIds = state.Models.Values
            .Where(model => model.Enabled && string.Equals(model.PublicName, publicModelName, StringComparison.Ordinal))
            .Select(model => model.Id)
            .ToHashSet();

        if (modelIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DeploymentCandidate>>([]);
        }

        var candidates = new List<DeploymentCandidate>();
        foreach (var deployment in state.Deployments.Values)
        {
            if (!deployment.Enabled || !modelIds.Contains(deployment.ModelId))
            {
                continue;
            }

            if (!state.Nodes.TryGetValue(deployment.NodeId, out var node) || !node.Enabled)
            {
                continue;
            }

            if (!state.Models.TryGetValue(deployment.ModelId, out var model) || !model.Enabled)
            {
                continue;
            }

            candidates.Add(new DeploymentCandidate(
                deployment.Id,
                node.Id,
                node.Name,
                node.BaseAddress,
                model.Id,
                model.PublicName,
                model.ProviderModelName,
                EffectiveWeight(node.Weight, deployment.Weight),
                deployment.MaxConcurrency ?? node.MaxConcurrency,
                node.Status,
                node.MaxConcurrency));
        }

        return Task.FromResult<IReadOnlyList<DeploymentCandidate>>(candidates);
    }

    public Task<IReadOnlyList<PublicModel>> GetPublicModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Volatile.Read(ref _state);
        IReadOnlyList<PublicModel> models = state.Models.Values
            .Where(model => model.Enabled)
            .OrderBy(model => model.PublicName, StringComparer.Ordinal)
            .Select(model => new PublicModel(
                model.Id,
                model.PublicName,
                model.SupportsStreaming,
                model.SupportsTools))
            .ToArray();
        return Task.FromResult(models);
    }

    public RouteCatalogStatus GetStatus()
    {
        var state = Volatile.Read(ref _state);
        return new RouteCatalogStatus(
            state.Version,
            state.Nodes.Count,
            state.Models.Count,
            state.Deployments.Count);
    }

    public void Replace(
        IEnumerable<RouteNodeSnapshot> nodes,
        IEnumerable<RouteModelSnapshot> models,
        IEnumerable<RouteDeploymentSnapshot> deployments)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(deployments);

        lock (_gate)
        {
            var nextVersion = _state.Version + 1;
            Volatile.Write(ref _state, new CatalogState(
                nextVersion,
                nodes.ToDictionary(node => node.Id),
                models.ToDictionary(model => model.Id),
                deployments.ToDictionary(deployment => deployment.Id)));
        }
    }

    public void Upsert(RouteNodeSnapshot node)
        => Mutate(state => state with { Nodes = CopyAndSet(state.Nodes, node.Id, node) });

    public void Upsert(RouteModelSnapshot model)
        => Mutate(state => state with { Models = CopyAndSet(state.Models, model.Id, model) });

    public void Upsert(RouteDeploymentSnapshot deployment)
        => Mutate(state => state with { Deployments = CopyAndSet(state.Deployments, deployment.Id, deployment) });

    public void RemoveNode(Guid nodeId)
        => Mutate(state => state with { Nodes = CopyAndRemove(state.Nodes, nodeId) });

    public void RemoveModel(Guid modelId)
        => Mutate(state => state with { Models = CopyAndRemove(state.Models, modelId) });

    public void RemoveDeployment(Guid deploymentId)
        => Mutate(state => state with { Deployments = CopyAndRemove(state.Deployments, deploymentId) });

    private void Mutate(Func<CatalogState, CatalogState> mutation)
    {
        lock (_gate)
        {
            var current = _state;
            var next = mutation(current) with { Version = current.Version + 1 };
            Volatile.Write(ref _state, next);
        }
    }

    private static IReadOnlyDictionary<Guid, T> CopyAndSet<T>(
        IReadOnlyDictionary<Guid, T> source,
        Guid id,
        T value)
    {
        var copy = source.ToDictionary(item => item.Key, item => item.Value);
        copy[id] = value;
        return copy;
    }

    private static IReadOnlyDictionary<Guid, T> CopyAndRemove<T>(
        IReadOnlyDictionary<Guid, T> source,
        Guid id)
    {
        var copy = source.ToDictionary(item => item.Key, item => item.Value);
        copy.Remove(id);
        return copy;
    }

    private static CatalogState Empty()
        => new(
            0,
            new Dictionary<Guid, RouteNodeSnapshot>(),
            new Dictionary<Guid, RouteModelSnapshot>(),
            new Dictionary<Guid, RouteDeploymentSnapshot>());

    private static int EffectiveWeight(int nodeWeight, int deploymentWeight)
        => (int)Math.Min((long)nodeWeight * deploymentWeight, int.MaxValue);
}
