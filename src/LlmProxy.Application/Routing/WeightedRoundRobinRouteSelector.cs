using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Application.Routing;

public sealed class WeightedRoundRobinRouteSelector : IRouteSelector
{
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);

    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
    {
        var eligible = RouteSelectorSupport.Eligible(candidates, loadTracker);
        if (eligible.Count == 0)
        {
            return null;
        }

        var totalWeight = eligible.Sum(candidate => (long)candidate.Weight);
        var key = eligible[0].PublicModelName;
        var sequence = _counters.AddOrUpdate(key, 0, static (_, current) => current == long.MaxValue ? 0 : current + 1);
        var slot = sequence % totalWeight;

        foreach (var candidate in eligible)
        {
            if (slot < candidate.Weight)
            {
                return RouteSelectorSupport.ToSelection(candidate);
            }

            slot -= candidate.Weight;
        }

        return RouteSelectorSupport.ToSelection(eligible[^1]);
    }
}
