using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Application.Routing;

public sealed class WeightedLeastLoadedRouteSelector : IRouteSelector
{
    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
    {
        var selected = candidates
            .Where(candidate => candidate.NodeStatus is NodeStatus.Healthy or NodeStatus.Degraded or NodeStatus.Unknown)
            .Select(candidate => new
            {
                Candidate = candidate,
                Active = loadTracker.GetActive(candidate.DeploymentId)
            })
            .Where(item => item.Active < item.Candidate.MaxConcurrency)
            .OrderBy(item => Score(item.Active, item.Candidate.MaxConcurrency, item.Candidate.Weight))
            .ThenBy(item => item.Active)
            .ThenBy(item => item.Candidate.NodeName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Candidate)
            .FirstOrDefault();

        return selected is null
            ? null
            : new RouteSelection(
                selected.DeploymentId,
                selected.NodeId,
                selected.NodeName,
                selected.BaseAddress,
                selected.PublicModelName,
                selected.ProviderModelName,
                selected.MaxConcurrency);
    }

    private static double Score(int active, int maxConcurrency, int weight)
        => (active + 1d) / (maxConcurrency * weight);
}
