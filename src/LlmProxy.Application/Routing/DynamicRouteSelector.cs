using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Routing;

namespace LlmProxy.Application.Routing;

public sealed class DynamicRouteSelector(
    RoutingStrategyState strategyState,
    IDeploymentPerformanceTracker? performanceTracker = null) : IRouteSelector
{
    private readonly WeightedLeastLoadedRouteSelector _weightedLeastLoaded = new(performanceTracker);
    private readonly RoundRobinRouteSelector _roundRobin = new();
    private readonly WeightedRoundRobinRouteSelector _weightedRoundRobin = new();

    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
        => strategyState.Current switch
        {
            RoutingStrategy.WeightedLeastLoaded => _weightedLeastLoaded.Select(candidates, loadTracker),
            RoutingStrategy.RoundRobin => _roundRobin.Select(candidates, loadTracker),
            RoutingStrategy.WeightedRoundRobin => _weightedRoundRobin.Select(candidates, loadTracker),
            _ => throw new InvalidOperationException($"Unsupported routing strategy '{strategyState.Current}'.")
        };
}
