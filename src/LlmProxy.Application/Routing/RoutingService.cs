using LlmProxy.Application.Abstractions;

namespace LlmProxy.Application.Routing;

public sealed class RoutingService(
    IDeploymentCatalog catalog,
    IRouteSelector selector,
    IRequestLoadTracker loadTracker)
{
    public async Task<RouteSelection?> SelectAsync(string publicModelName, CancellationToken cancellationToken)
    {
        var candidates = await catalog.GetCandidatesAsync(publicModelName, cancellationToken);
        return selector.Select(candidates, loadTracker);
    }
}
