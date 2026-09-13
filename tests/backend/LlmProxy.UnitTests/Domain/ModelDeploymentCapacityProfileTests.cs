using LlmProxy.Domain.Deployments;

namespace LlmProxy.UnitTests.Domain;

public sealed class ModelDeploymentCapacityProfileTests
{
    [Fact]
    public void Capacity_profile_is_recommendation_until_explicitly_applied()
    {
        var deployment = new ModelDeployment(Guid.NewGuid(), Guid.NewGuid(), weight: 2, maxConcurrency: 4);
        var measuredAt = DateTimeOffset.UtcNow;

        deployment.SetCapacityProfile(
            recommendedMaxConcurrency: 8,
            p95TtftMilliseconds: 420,
            p95DurationMilliseconds: 4800,
            sustainableOutputTokensPerSecond: 92,
            benchmarkSource: "benchmark-results/run-001.json",
            measuredAt);

        Assert.Equal(4, deployment.MaxConcurrency);
        Assert.Equal(8, deployment.RecommendedMaxConcurrency);
        Assert.Equal(420, deployment.BenchmarkP95TtftMilliseconds);
        Assert.Equal(4800, deployment.BenchmarkP95DurationMilliseconds);
        Assert.Equal(92, deployment.SustainableOutputTokensPerSecond);
        Assert.Equal("benchmark-results/run-001.json", deployment.BenchmarkSource);
        Assert.Equal(measuredAt, deployment.BenchmarkMeasuredAtUtc);

        deployment.ApplyRecommendedCapacity();

        Assert.Equal(8, deployment.MaxConcurrency);
        Assert.Equal(2, deployment.Weight);
    }

    [Fact]
    public void Clearing_profile_does_not_change_active_capacity()
    {
        var deployment = new ModelDeployment(Guid.NewGuid(), Guid.NewGuid(), maxConcurrency: 4);
        deployment.SetCapacityProfile(8, 400, 4000, 80, "run-001", DateTimeOffset.UtcNow);
        deployment.ApplyRecommendedCapacity();

        deployment.ClearCapacityProfile();

        Assert.Equal(8, deployment.MaxConcurrency);
        Assert.Null(deployment.RecommendedMaxConcurrency);
        Assert.Null(deployment.BenchmarkSource);
    }

    [Fact]
    public void Capacity_profile_rejects_invalid_measurements()
    {
        var deployment = new ModelDeployment(Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            deployment.SetCapacityProfile(0, null, null, null, "run", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            deployment.SetCapacityProfile(1, -1, null, null, "run", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            deployment.SetCapacityProfile(1, null, null, null, " ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Applying_without_profile_is_rejected()
    {
        var deployment = new ModelDeployment(Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => deployment.ApplyRecommendedCapacity());
    }
}
