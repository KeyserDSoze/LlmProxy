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
    NodeStatus NodeStatus,
    int NodeMaxConcurrency = int.MaxValue);

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
    int MaxConcurrency,
    int NodeMaxConcurrency = int.MaxValue);

public enum RoutingSelectionFailure
{
    None = 0,
    Unavailable = 1,
    CapacityExhausted = 2
}

public sealed record RoutingSelectionResult(RouteSelection? Route, RoutingSelectionFailure Failure)
{
    public static RoutingSelectionResult Success(RouteSelection route) => new(route, RoutingSelectionFailure.None);
    public static RoutingSelectionResult Failed(RoutingSelectionFailure failure) => new(null, failure);
}
