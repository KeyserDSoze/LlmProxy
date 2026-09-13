using LlmProxy.Application.Abstractions;

namespace LlmProxy.Application.Routing;

public sealed class RoutingService(
    IDeploymentCatalog catalog,
    IRouteSelector selector,
    IRequestLoadTracker loadTracker)
{
    public async Task<RouteSelection?> SelectAsync(string publicModelName, CancellationToken cancellationToken)
        => (await SelectDetailedAsync(publicModelName, excludedDeploymentIds: null, cancellationToken)).Route;

    public async Task<RouteSelection?> SelectAsync(
        string publicModelName,
        IReadOnlySet<Guid>? excludedDeploymentIds,
        CancellationToken cancellationToken)
        => (await SelectDetailedAsync(publicModelName, excludedDeploymentIds, cancellationToken)).Route;

    public async Task<RoutingSelectionResult> SelectDetailedAsync(
        string publicModelName,
        IReadOnlySet<Guid>? excludedDeploymentIds,
        CancellationToken cancellationToken)
    {
        var candidates = await catalog.GetCandidatesAsync(publicModelName, cancellationToken);
        if (excludedDeploymentIds is { Count: > 0 })
        {
            candidates = candidates.Where(candidate => !excludedDeploymentIds.Contains(candidate.DeploymentId)).ToArray();
        }

        var selected = selector.Select(candidates, loadTracker);
        if (selected is not null)
        {
            return RoutingSelectionResult.Success(selected);
        }

        var operational = candidates.Where(RouteSelectorSupport.IsOperational).ToArray();
        if (operational.Length > 0 && operational.All(candidate =>
                !RouteSelectorSupport.HasDeploymentCapacity(candidate, loadTracker) ||
                !RouteSelectorSupport.HasNodeCapacity(candidate, loadTracker)))
        {
            return RoutingSelectionResult.Failed(RoutingSelectionFailure.CapacityExhausted);
        }

        return RoutingSelectionResult.Failed(RoutingSelectionFailure.Unavailable);
    }
}
