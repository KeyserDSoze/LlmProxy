using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Application.Routing;

public sealed class WeightedLeastLoadedRouteSelector(
    IDeploymentPerformanceTracker? performanceTracker = null) : IRouteSelector
{
    private readonly IDeploymentPerformanceTracker _performanceTracker =
        performanceTracker ?? NullDeploymentPerformanceTracker.Instance;

    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
    {
        var selected = RouteSelectorSupport.Eligible(candidates, loadTracker)
            .Select(candidate => new
            {
                Candidate = candidate,
                Active = loadTracker.GetActive(candidate.DeploymentId),
                Performance = _performanceTracker.GetSnapshot(candidate.DeploymentId)
            })
            .OrderBy(item => Score(item.Candidate, item.Active, item.Performance))
            .ThenBy(item => item.Active)
            .ThenBy(item => item.Candidate.NodeName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Candidate)
            .FirstOrDefault();

        return selected is null ? null : RouteSelectorSupport.ToSelection(selected);
    }

    internal static double Score(
        DeploymentCandidate candidate,
        int active,
        DeploymentPerformanceSnapshot performance)
    {
        var loadScore = (active + 1d) / (candidate.MaxConcurrency * candidate.Weight);
        var healthPenalty = candidate.NodeStatus switch
        {
            NodeStatus.Degraded => 0.35d,
            NodeStatus.Unknown => 0.10d,
            _ => 0d
        };

        // Ignore very small samples so a single slow warm-up request cannot permanently bias routing.
        if (performance.SampleCount < 3)
        {
            return loadScore + healthPenalty;
        }

        var latencyPenalty = performance.EwmaTimeToFirstByteMilliseconds is double timeToFirstByte
            ? Math.Min(timeToFirstByte / 2_000d, 1d) * 0.25d
            : 0d;
        var failurePenalty = Math.Clamp(performance.InfrastructureFailureScore, 0d, 1d) * 1.50d;

        return loadScore + healthPenalty + latencyPenalty + failurePenalty;
    }
}
