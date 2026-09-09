namespace LlmProxy.Application.Abstractions;

public sealed record DeploymentPerformanceSnapshot(
    Guid DeploymentId,
    long SampleCount,
    double? EwmaTimeToFirstByteMilliseconds,
    double? EwmaDurationMilliseconds,
    double InfrastructureFailureScore,
    DateTimeOffset? LastObservedAtUtc);

public interface IDeploymentPerformanceTracker
{
    DeploymentPerformanceSnapshot GetSnapshot(Guid deploymentId);

    IReadOnlyList<DeploymentPerformanceSnapshot> GetSnapshots();

    void Observe(
        Guid deploymentId,
        bool infrastructureHealthy,
        long durationMilliseconds,
        long? timeToFirstByteMilliseconds,
        DateTimeOffset observedAtUtc);
}

public sealed class NullDeploymentPerformanceTracker : IDeploymentPerformanceTracker
{
    public static NullDeploymentPerformanceTracker Instance { get; } = new();

    private NullDeploymentPerformanceTracker()
    {
    }

    public DeploymentPerformanceSnapshot GetSnapshot(Guid deploymentId) =>
        new(deploymentId, 0, null, null, 0d, null);

    public IReadOnlyList<DeploymentPerformanceSnapshot> GetSnapshots() => [];

    public void Observe(
        Guid deploymentId,
        bool infrastructureHealthy,
        long durationMilliseconds,
        long? timeToFirstByteMilliseconds,
        DateTimeOffset observedAtUtc)
    {
    }
}
