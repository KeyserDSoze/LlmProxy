using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Observability;

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
        using var activity = LlmProxyActivity.Start("llmproxy.routing.select");
        activity?.SetTag("llmproxy.logical_model", publicModelName);
        activity?.SetTag("llmproxy.routing.excluded_deployments", excludedDeploymentIds?.Count ?? 0);

        var candidates = await catalog.GetCandidatesAsync(publicModelName, cancellationToken);
        activity?.SetTag("llmproxy.routing.candidates_before_exclusion", candidates.Count);

        if (excludedDeploymentIds is { Count: > 0 })
        {
            candidates = candidates.Where(candidate => !excludedDeploymentIds.Contains(candidate.DeploymentId)).ToArray();
        }

        activity?.SetTag("llmproxy.routing.candidates", candidates.Count);
        var selected = selector.Select(candidates, loadTracker);
        if (selected is not null)
        {
            activity?.SetTag("llmproxy.routing.result", "selected");
            LlmProxyActivity.SetGuid(activity, "llmproxy.deployment.id", selected.DeploymentId);
            LlmProxyActivity.SetGuid(activity, "llmproxy.node.id", selected.NodeId);
            activity?.SetTag("llmproxy.node.name", selected.NodeName);
            return RoutingSelectionResult.Success(selected);
        }

        var operational = candidates.Where(RouteSelectorSupport.IsOperational).ToArray();
        if (operational.Length > 0 && operational.All(candidate =>
                !RouteSelectorSupport.HasDeploymentCapacity(candidate, loadTracker) ||
                !RouteSelectorSupport.HasNodeCapacity(candidate, loadTracker)))
        {
            activity?.SetTag("llmproxy.routing.result", "capacity_exhausted");
            LlmProxyActivity.MarkError(activity, "capacity_exhausted");
            return RoutingSelectionResult.Failed(RoutingSelectionFailure.CapacityExhausted);
        }

        activity?.SetTag("llmproxy.routing.result", "unavailable");
        LlmProxyActivity.MarkError(activity, "no_healthy_deployment");
        return RoutingSelectionResult.Failed(RoutingSelectionFailure.Unavailable);
    }
}
