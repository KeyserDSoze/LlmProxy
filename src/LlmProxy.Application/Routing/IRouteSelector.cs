using LlmProxy.Application.Abstractions;

namespace LlmProxy.Application.Routing;

public interface IRouteSelector
{
    RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker);
}
