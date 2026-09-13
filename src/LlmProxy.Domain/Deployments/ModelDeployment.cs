namespace LlmProxy.Domain.Deployments;

public sealed class ModelDeployment
{
    private ModelDeployment()
    {
    }

    public ModelDeployment(Guid nodeId, Guid modelId, int weight = 1, int? maxConcurrency = null)
    {
        if (nodeId == Guid.Empty)
        {
            throw new ArgumentException("Node id is required.", nameof(nodeId));
        }

        if (modelId == Guid.Empty)
        {
            throw new ArgumentException("Model id is required.", nameof(modelId));
        }

        NodeId = nodeId;
        ModelId = modelId;
        SetCapacity(weight, maxConcurrency);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid NodeId { get; private set; }
    public Guid ModelId { get; private set; }
    public bool Enabled { get; private set; } = true;
    public int Weight { get; private set; } = 1;
    public int? MaxConcurrency { get; private set; }

    public int? RecommendedMaxConcurrency { get; private set; }
    public double? BenchmarkP95TtftMilliseconds { get; private set; }
    public double? BenchmarkP95DurationMilliseconds { get; private set; }
    public double? SustainableOutputTokensPerSecond { get; private set; }
    public string? BenchmarkSource { get; private set; }
    public DateTimeOffset? BenchmarkMeasuredAtUtc { get; private set; }

    public void SetCapacity(int weight, int? maxConcurrency)
    {
        if (weight < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be at least 1.");
        }

        if (maxConcurrency is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Max concurrency must be null or at least 1.");
        }

        Weight = weight;
        MaxConcurrency = maxConcurrency;
    }

    public void SetCapacityProfile(
        int recommendedMaxConcurrency,
        double? p95TtftMilliseconds,
        double? p95DurationMilliseconds,
        double? sustainableOutputTokensPerSecond,
        string benchmarkSource,
        DateTimeOffset measuredAtUtc)
    {
        if (recommendedMaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(recommendedMaxConcurrency), "Recommended concurrency must be at least 1.");
        }

        ValidateNonNegative(p95TtftMilliseconds, nameof(p95TtftMilliseconds));
        ValidateNonNegative(p95DurationMilliseconds, nameof(p95DurationMilliseconds));
        ValidateNonNegative(sustainableOutputTokensPerSecond, nameof(sustainableOutputTokensPerSecond));
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkSource);

        if (benchmarkSource.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(benchmarkSource), "Benchmark source must be at most 500 characters.");
        }

        RecommendedMaxConcurrency = recommendedMaxConcurrency;
        BenchmarkP95TtftMilliseconds = p95TtftMilliseconds;
        BenchmarkP95DurationMilliseconds = p95DurationMilliseconds;
        SustainableOutputTokensPerSecond = sustainableOutputTokensPerSecond;
        BenchmarkSource = benchmarkSource.Trim();
        BenchmarkMeasuredAtUtc = measuredAtUtc;
    }

    public void ClearCapacityProfile()
    {
        RecommendedMaxConcurrency = null;
        BenchmarkP95TtftMilliseconds = null;
        BenchmarkP95DurationMilliseconds = null;
        SustainableOutputTokensPerSecond = null;
        BenchmarkSource = null;
        BenchmarkMeasuredAtUtc = null;
    }

    public void ApplyRecommendedCapacity()
    {
        if (RecommendedMaxConcurrency is not int recommended)
        {
            throw new InvalidOperationException("No capacity profile recommendation is available for this deployment.");
        }

        SetCapacity(Weight, recommended);
    }

    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;

    private static void ValidateNonNegative(double? value, string parameterName)
    {
        if (value is < 0 || value is double.NaN or double.PositiveInfinity or double.NegativeInfinity)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Benchmark values must be finite and non-negative when provided.");
        }
    }
}
