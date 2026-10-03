using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Application.Abstractions;

public sealed record RouteNodeSnapshot(
    Guid Id,
    string Name,
    string BaseAddress,
    bool Enabled,
    NodeStatus Status,
    int Weight,
    int MaxConcurrency,
    string? UpstreamBearerTokenCiphertext = null)
{
    public static RouteNodeSnapshot From(InferenceNode node)
        => new(
            node.Id,
            node.Name,
            node.BaseAddress,
            node.Enabled,
            node.Status,
            node.Weight,
            node.MaxConcurrency,
            node.UpstreamBearerTokenCiphertext);
}

public sealed record RouteModelSnapshot(
    Guid Id,
    string PublicName,
    string ProviderModelName,
    bool Enabled,
    bool SupportsStreaming,
    bool SupportsTools)
{
    public static RouteModelSnapshot From(ModelDefinition model)
        => new(
            model.Id,
            model.PublicName,
            model.ProviderModelName,
            model.Enabled,
            model.SupportsStreaming,
            model.SupportsTools);
}

public sealed record RouteDeploymentSnapshot(
    Guid Id,
    Guid NodeId,
    Guid ModelId,
    bool Enabled,
    int Weight,
    int? MaxConcurrency,
    string? RuntimeBaseAddress)
{
    public static RouteDeploymentSnapshot From(ModelDeployment deployment)
        => new(
            deployment.Id,
            deployment.NodeId,
            deployment.ModelId,
            deployment.Enabled,
            deployment.Weight,
            deployment.MaxConcurrency,
            deployment.RuntimeBaseAddress);
}

public sealed record RouteCatalogStatus(
    long Version,
    int NodeCount,
    int ModelCount,
    int DeploymentCount);

public interface IRouteCatalog : IDeploymentCatalog
{
    RouteCatalogStatus GetStatus();

    void Replace(
        IEnumerable<RouteNodeSnapshot> nodes,
        IEnumerable<RouteModelSnapshot> models,
        IEnumerable<RouteDeploymentSnapshot> deployments);

    void Upsert(RouteNodeSnapshot node);
    void Upsert(RouteModelSnapshot model);
    void Upsert(RouteDeploymentSnapshot deployment);
    void RemoveNode(Guid nodeId);
    void RemoveModel(Guid modelId);
    void RemoveDeployment(Guid deploymentId);
}
