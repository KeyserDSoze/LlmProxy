using LlmProxy.Benchmarking;

namespace LlmProxy.Benchmark.Tests;

public sealed class BenchmarkCapacityEvaluatorTests
{
    [Fact]
    public void Recommends_largest_contiguous_level_satisfying_slos()
    {
        var report = Report(Level(1, 800, 100), Level(4, 1500, 88), Level(8, 7000, 150), Level(12, 2000, 240));
        var recommendation = BenchmarkCapacityEvaluator.Evaluate(report, Options())!;
        Assert.Equal(4, recommendation.RecommendedMaxConcurrency);
        Assert.Collection(recommendation.Levels, a => Assert.True(a.Pass), b => Assert.True(b.Pass),
            c => Assert.False(c.Pass), d => Assert.True(d.Pass));
    }

    [Fact]
    public void Rejects_missing_token_usage_when_token_slo_is_required()
    {
        var level = Level(1, 1000, 100) with { OutputTokensPerSecond = null };
        var recommendation = BenchmarkCapacityEvaluator.Evaluate(Report(level), Options())!;
        Assert.Null(recommendation.RecommendedMaxConcurrency);
        Assert.Contains("unavailable", recommendation.Levels[0].Reason);
    }

    [Fact]
    public void Small_samples_do_not_prove_twelve_way_capacity()
    {
        var level = Level(12, 2000, 240) with { Attempted = 12 };
        var result = BenchmarkCapacityEvaluator.Evaluate(Report(level), Options())!;
        Assert.Null(result.RecommendedMaxConcurrency);
        Assert.Contains("needs >= 24", result.Levels[0].Reason);
    }

    private static BenchmarkOptions Options() => new()
    {
        Target = new Uri("http://localhost:8000"),
        Model = "agic-code",
        ConcurrencyLevels = [1, 4, 8, 12],
        MaxP95TtftMilliseconds = 5000,
        MinOutputTokensPerSecondPerSlot = 10
    };

    private static BenchmarkLevelReport Level(int concurrency, double ttftMs, double outputTps) =>
        new(concurrency, 30, 30, 0, 100, 1000, 30, ttftMs, ttftMs, ttftMs,
            1000, 1000, 1000, 100, 100, 200, 30, outputTps, new Dictionary<string, int>());

    private static BenchmarkReport Report(params BenchmarkLevelReport[] levels) =>
        new("id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "http://localhost:8000",
            "agic-code", "chat_completions", true, 30, 2, 128, "synthetic", 60, levels);
}
