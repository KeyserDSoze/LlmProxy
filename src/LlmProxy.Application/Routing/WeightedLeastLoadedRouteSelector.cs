using LlmProxy.Application.Abstractions;

namespace LlmProxy.Application.Routing;

public sealed class WeightedLeastLoadedRouteSelector : IRouteSelector
{
    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
    {
        var selected = RouteSelectorSupport.Eligible(candidates, loadTracker)
            .Select(candidate => new
            {
                Candidate = candidate,
                Active = loadTracker.GetActive(candidate.DeploymentId)
            })
            .OrderBy(item => Score(item.Active, item.Candidate.MaxConcurrency, item.Candidate.Weight))
            .ThenBy(item => item.Active)
            .ThenBy(item => item.Candidate.NodeName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Candidate)
            .FirstOrDefault();

        return selected is null ? null : RouteSelectorSupport.ToSelection(selected);
    }

    private static double Score(int active, int maxConcurrency, int weight)
        => (active + 1d) / (maxConcurrency * weight);
}
