using LlmProxy.Domain.Nodes;

namespace LlmProxy.Application.Routing;

public sealed record DeploymentCandidate(
    Guid DeploymentId,
    Guid NodeId,
    string NodeName,
    string BaseAddress,
    Guid ModelId,
    string PublicModelName,
    string ProviderModelName,
    int Weight,
    int MaxConcurrency,
    NodeStatus NodeStatus);

public sealed record PublicModel(
    Guid Id,
    string PublicName,
    bool SupportsStreaming,
    bool SupportsTools);

public sealed record RouteSelection(
    Guid DeploymentId,
    Guid NodeId,
    string NodeName,
    string BaseAddress,
    string PublicModelName,
    string ProviderModelName,
    int MaxConcurrency);
