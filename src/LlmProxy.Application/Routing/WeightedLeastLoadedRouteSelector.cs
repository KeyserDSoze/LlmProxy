using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;

namespace LlmProxy.Application.Routing;

public sealed class WeightedLeastLoadedRouteSelector(
    IDeploymentPerformanceTracker? performanceTracker = null,
    INodeRuntimeMetricsTracker? runtimeMetricsTracker = null,
    RoutingTuningState? tuningState = null) : IRouteSelector
{
    private readonly IDeploymentPerformanceTracker _performanceTracker =
        performanceTracker ?? NullDeploymentPerformanceTracker.Instance;
    private readonly INodeRuntimeMetricsTracker _runtimeMetricsTracker =
        runtimeMetricsTracker ?? NullNodeRuntimeMetricsTracker.Instance;
    private readonly RoutingTuningState _tuningState =
        tuningState ?? new RoutingTuningState(RoutingTuningSettings.Default);

    public RouteSelection? Select(IReadOnlyList<DeploymentCandidate> candidates, IRequestLoadTracker loadTracker)
    {
        var tuning = _tuningState.Current;
        var selected = RouteSelectorSupport.Eligible(candidates, loadTracker)
            .Select(candidate => new
            {
                Candidate = candidate,
                Active = loadTracker.GetActive(candidate.DeploymentId),
                Performance = _performanceTracker.GetSnapshot(candidate.DeploymentId),
                Runtime = _runtimeMetricsTracker.GetSnapshot(candidate.NodeId)
            })
            .OrderBy(item => Score(item.Candidate, item.Active, item.Performance, item.Runtime, tuning))
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
        NodeRuntimeMetricsSnapshot? runtime = null,
        RoutingTuningSettings? tuning = null)
    {
        var settings = tuning ?? RoutingTuningSettings.Default;
        var loadScore = (active + 1d) / (candidate.MaxConcurrency * candidate.Weight);
        var healthPenalty = candidate.NodeStatus switch
        {
            NodeStatus.Degraded => settings.DegradedNodePenalty,
            NodeStatus.Unknown => settings.UnknownNodePenalty,
            _ => 0d
        };

        var performancePenalty = 0d;
        if (performance.SampleCount >= settings.WarmupSamples)
        {
            var latencyPenalty = performance.EwmaTimeToFirstByteMilliseconds is double timeToFirstByte
                ? Math.Min(timeToFirstByte / settings.TtftTargetMilliseconds, 1d) * settings.TtftPenaltyWeight
                : 0d;
            var failurePenalty = Math.Clamp(performance.InfrastructureFailureScore, 0d, 1d) * settings.FailurePenaltyWeight;
            performancePenalty = latencyPenalty + failurePenalty;
        }

        var runtimePenalty = 0d;
        if (runtime?.Available == true)
        {
            var externalRunning = Math.Max(0d, runtime.RunningRequests - active);
            var externalLoadPenalty = Math.Min(externalRunning / candidate.MaxConcurrency, 1d) * settings.ExternalLoadPenaltyWeight;
            var queuePenalty = Math.Min(runtime.WaitingRequests / candidate.MaxConcurrency, 2d) * settings.QueuePenaltyWeight;
            var kvPenalty = runtime.KvCacheUsageRatio is double kv && kv > settings.KvCacheThreshold
                ? Math.Min((kv - settings.KvCacheThreshold) / Math.Max(1d - settings.KvCacheThreshold, 0.01d), 1d) * settings.KvCachePenaltyWeight
                : 0d;
            runtimePenalty = externalLoadPenalty + queuePenalty + kvPenalty;
        }

        return loadScore + healthPenalty + performancePenalty + runtimePenalty;
    }
}
