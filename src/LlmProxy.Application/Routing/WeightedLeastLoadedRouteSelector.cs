using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Application.Routing;

public sealed class WeightedLeastLoadedRouteSelector(
    IDeploymentPerformanceTracker? performanceTracker = null,
    INodeRuntimeMetricsTracker? runtimeMetricsTracker = null) : IRouteSelector
{
    private readonly IDeploymentPerformanceTracker _performanceTracker =
        performanceTracker ?? NullDeploymentPerformanceTracker.Instance;
    private readonly INodeRuntimeMetricsTracker _runtimeMetricsTracker =
        runtimeMetricsTracker ?? NullNodeRuntimeMetricsTracker.Instance;

    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
    {
        var selected = RouteSelectorSupport.Eligible(candidates, loadTracker)
            .Select(candidate => new
            {
                Candidate = candidate,
                Active = loadTracker.GetActive(candidate.DeploymentId),
                Performance = _performanceTracker.GetSnapshot(candidate.DeploymentId),
                Runtime = _runtimeMetricsTracker.GetSnapshot(candidate.NodeId)
            })
            .OrderBy(item => Score(item.Candidate, item.Active, item.Performance, item.Runtime))
            .ThenBy(item => item.Active)
            .ThenBy(item => item.Candidate.NodeName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Candidate)
            .FirstOrDefault();

        return selected is null ? null : RouteSelectorSupport.ToSelection(selected);
    }

    internal static double Score(
        DeploymentCandidate candidate,
        int active,
        DeploymentPerformanceSnapshot performance,
        NodeRuntimeMetricsSnapshot? runtime = null)
    {
        var loadScore = (active + 1d) / (candidate.MaxConcurrency * candidate.Weight);
        var healthPenalty = candidate.NodeStatus switch
        {
            NodeStatus.Degraded => 0.35d,
            NodeStatus.Unknown => 0.10d,
            _ => 0d
        };

        var performancePenalty = 0d;
        if (performance.SampleCount >= 3)
        {
            var latencyPenalty = performance.EwmaTimeToFirstByteMilliseconds is double timeToFirstByte
                ? Math.Min(timeToFirstByte / 2_000d, 1d) * 0.25d
                : 0d;
            var failurePenalty = Math.Clamp(performance.InfrastructureFailureScore, 0d, 1d) * 1.50d;
            performancePenalty = latencyPenalty + failurePenalty;
        }

        var runtimePenalty = 0d;
        if (runtime?.Available == true)
        {
            // Gateway active-request accounting is authoritative for traffic flowing through us.
            // vLLM running requests beyond that number reveal work coming from other clients.
            var externalRunning = Math.Max(0d, runtime.RunningRequests - active);
            var externalLoadPenalty = Math.Min(externalRunning / candidate.MaxConcurrency, 1d) * 0.40d;
            var queuePenalty = Math.Min(runtime.WaitingRequests / candidate.MaxConcurrency, 2d) * 0.75d;
            var kvPenalty = runtime.KvCacheUsageRatio is double kv && kv > 0.70d
                ? Math.Min((kv - 0.70d) / 0.30d, 1d) * 0.60d
                : 0d;
            runtimePenalty = externalLoadPenalty + queuePenalty + kvPenalty;
        }

        return loadScore + healthPenalty + performancePenalty + runtimePenalty;
    }
}
