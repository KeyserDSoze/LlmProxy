using LlmProxy.Application.Abstractions;

namespace LlmProxy.Application.Routing;

public sealed class RoutingService(
    IDeploymentCatalog catalog,
    IRouteSelector selector,
    IRequestLoadTracker loadTracker)
{
    public Task<RouteSelection?> SelectAsync(string publicModelName, CancellationToken cancellationToken)
        => SelectAsync(publicModelName, excludedDeploymentIds: null, cancellationToken);

    public async Task<RouteSelection?> SelectAsync(
        string publicModelName,
        IReadOnlySet<Guid>? excludedDeploymentIds,
        CancellationToken cancellationToken)
    {
        var candidates = await catalog.GetCandidatesAsync(publicModelName, cancellationToken);
        if (excludedDeploymentIds is { Count: > 0 })
        {
            candidates = candidates.Where(candidate => !excludedDeploymentIds.Contains(candidate.DeploymentId)).ToArray();
        }

        return selector.Select(candidates, loadTracker);
    }
}
