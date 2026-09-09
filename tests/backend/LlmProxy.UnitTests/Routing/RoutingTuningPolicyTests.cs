using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;
using LlmProxy.Infrastructure.Telemetry;

namespace LlmProxy.UnitTests.Routing;

public sealed class RoutingTuningPolicyTests
{
    [Fact]
    public void Default_settings_are_valid_and_round_trip_through_policy()
    {
        var policy = new RoutingTuningPolicy(RoutingTuningSettings.Default);

        Assert.Equal(RoutingTuningSettings.Default, policy.ToSettings());
        Assert.Equal(RoutingTuningPolicy.SingletonId, policy.Id);
    }

    [Fact]
    public void Invalid_kv_threshold_is_rejected()
    {
        var invalid = RoutingTuningSettings.Default with { KvCacheThreshold = 1.1d };

        Assert.Throws<ArgumentOutOfRangeException>(() => new RoutingTuningPolicy(invalid));
    }

    [Fact]
    public void State_update_immediately_changes_runtime_routing_penalties()
    {
        var busyDeployment = Guid.NewGuid();
        var clearDeployment = Guid.NewGuid();
        var busyNode = Guid.NewGuid();
        var clearNode = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate(busyDeployment, busyNode, "a-busy"),
            Candidate(clearDeployment, clearNode, "z-clear")
        };
        var runtime = new VllmRuntimeMetricsTracker();
        var now = DateTimeOffset.UtcNow;
        runtime.RecordSuccess(busyNode, "model", 8, 6, 0.96d, 100, 50, now);
        runtime.RecordSuccess(clearNode, "model", 0, 0, 0.10d, 100, 50, now);
        var state = new RoutingTuningState(RoutingTuningSettings.Default);
        var selector = new WeightedLeastLoadedRouteSelector(runtimeMetricsTracker: runtime, tuningState: state);

        var withPressure = selector.Select(candidates, new EmptyLoadTracker());
        Assert.NotNull(withPressure);
        Assert.Equal(clearDeployment, withPressure.DeploymentId);

        state.Set(RoutingTuningSettings.Default with
        {
            ExternalLoadPenaltyWeight = 0,
            QueuePenaltyWeight = 0,
            KvCacheThreshold = 1,
            KvCachePenaltyWeight = 0
        });

        var withoutPressure = selector.Select(candidates, new EmptyLoadTracker());
        Assert.NotNull(withoutPressure);
        Assert.Equal(busyDeployment, withoutPressure.DeploymentId);
    }

    private static DeploymentCandidate Candidate(Guid deploymentId, Guid nodeId, string nodeName) => new(
        deploymentId,
        nodeId,
        nodeName,
        $"http://{nodeName}:8000",
        Guid.NewGuid(),
        "agic-code-fast",
        "provider-model",
        1,
        8,
        NodeStatus.Healthy);

    private sealed class EmptyLoadTracker : IRequestLoadTracker
    {
        public int GetActive(Guid deploymentId) => 0;
        public IDisposable Enter(Guid deploymentId) => Noop.Instance;
    }

    private sealed class Noop : IDisposable
    {
        public static Noop Instance { get; } = new();
        public void Dispose() { }
    }
}
