using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Application.Routing;

public sealed class RoundRobinRouteSelector : IRouteSelector
{
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);

    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
    {
        var eligible = RouteSelectorSupport.Eligible(candidates, loadTracker);
        if (eligible.Count == 0)
        {
            return null;
        }

        var key = eligible[0].PublicModelName;
        var sequence = _counters.AddOrUpdate(key, 0, static (_, current) => current == long.MaxValue ? 0 : current + 1);
        var selected = eligible[(int)(sequence % eligible.Count)];
        return RouteSelectorSupport.ToSelection(selected);
    }
}
