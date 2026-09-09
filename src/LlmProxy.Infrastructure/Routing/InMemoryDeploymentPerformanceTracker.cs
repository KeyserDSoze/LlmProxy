using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Infrastructure.Routing;

public sealed class InMemoryDeploymentPerformanceTracker : IDeploymentPerformanceTracker
{
    private const double Alpha = 0.25d;
    private readonly ConcurrentDictionary<Guid, State> _states = new();

    public DeploymentPerformanceSnapshot GetSnapshot(Guid deploymentId)
    {
        if (!_states.TryGetValue(deploymentId, out var state))
        {
            return new DeploymentPerformanceSnapshot(deploymentId, 0, null, null, 0d, null);
        }

        return state.Snapshot(deploymentId);
    }

    public IReadOnlyList<DeploymentPerformanceSnapshot> GetSnapshots() =>
        _states
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .OrderByDescending(snapshot => snapshot.LastObservedAtUtc)
            .ThenBy(snapshot => snapshot.DeploymentId)
            .ToArray();

    public void Observe(
        Guid deploymentId,
        bool infrastructureHealthy,
        long durationMilliseconds,
        long? timeToFirstByteMilliseconds,
        DateTimeOffset observedAtUtc)
    {
        if (deploymentId == Guid.Empty)
        {
            throw new ArgumentException("Deployment id is required.", nameof(deploymentId));
        }

        if (durationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        if (timeToFirstByteMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToFirstByteMilliseconds));
        }

        _states.GetOrAdd(deploymentId, static _ => new State())
            .Observe(infrastructureHealthy, durationMilliseconds, timeToFirstByteMilliseconds, observedAtUtc);
    }

    private sealed class State
    {
        private readonly object _gate = new();
        private long _sampleCount;
        private double? _ewmaTimeToFirstByteMilliseconds;
        private double? _ewmaDurationMilliseconds;
        private double _infrastructureFailureScore;
        private DateTimeOffset? _lastObservedAtUtc;

        public void Observe(
            bool infrastructureHealthy,
            long durationMilliseconds,
            long? timeToFirstByteMilliseconds,
            DateTimeOffset observedAtUtc)
        {
            lock (_gate)
            {
                _sampleCount++;
                _ewmaDurationMilliseconds = Ewma(_ewmaDurationMilliseconds, durationMilliseconds);
                if (timeToFirstByteMilliseconds is long timeToFirstByte)
                {
                    _ewmaTimeToFirstByteMilliseconds = Ewma(_ewmaTimeToFirstByteMilliseconds, timeToFirstByte);
                }

                var failureSample = infrastructureHealthy ? 0d : 1d;
                _infrastructureFailureScore = _sampleCount == 1
                    ? failureSample
                    : (Alpha * failureSample) + ((1d - Alpha) * _infrastructureFailureScore);
                _lastObservedAtUtc = observedAtUtc;
            }
        }

        public DeploymentPerformanceSnapshot Snapshot(Guid deploymentId)
        {
            lock (_gate)
            {
                return new DeploymentPerformanceSnapshot(
                    deploymentId,
                    _sampleCount,
                    _ewmaTimeToFirstByteMilliseconds,
                    _ewmaDurationMilliseconds,
                    _infrastructureFailureScore,
                    _lastObservedAtUtc);
            }
        }

        private static double Ewma(double? current, long sample) =>
            current is null ? sample : (Alpha * sample) + ((1d - Alpha) * current.Value);
    }
}
