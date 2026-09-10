using LlmProxy.Benchmarking;

namespace LlmProxy.Benchmark.Tests;

public sealed class BenchmarkStatisticsTests
{
    [Fact]
    public void Percentile_interpolates_ordered_samples()
    {
        var samples = new[] { 100d, 200d, 300d, 400d };

        Assert.Equal(250d, BenchmarkStatistics.Percentile(samples, 0.50d)!.Value, 6);
        Assert.Equal(385d, BenchmarkStatistics.Percentile(samples, 0.95d)!.Value, 6);
        Assert.Equal(397d, BenchmarkStatistics.Percentile(samples, 0.99d)!.Value, 6);
    }

    [Fact]
    public void Percentile_returns_null_for_empty_input()
    {
        Assert.Null(BenchmarkStatistics.Percentile(Array.Empty<double>(), 0.95d));
    }
}
