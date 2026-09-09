namespace LlmProxy.Domain.Routing;

public sealed record RoutingTuningSettings(
    int WarmupSamples,
    double TtftTargetMilliseconds,
    double TtftPenaltyWeight,
    double FailurePenaltyWeight,
    double ExternalLoadPenaltyWeight,
    double QueuePenaltyWeight,
    double KvCacheThreshold,
    double KvCachePenaltyWeight,
    double DegradedNodePenalty,
    double UnknownNodePenalty)
{
    public static RoutingTuningSettings Default { get; } = new(
        WarmupSamples: 3,
        TtftTargetMilliseconds: 2_000d,
        TtftPenaltyWeight: 0.25d,
        FailurePenaltyWeight: 1.50d,
        ExternalLoadPenaltyWeight: 0.40d,
        QueuePenaltyWeight: 0.75d,
        KvCacheThreshold: 0.70d,
        KvCachePenaltyWeight: 0.60d,
        DegradedNodePenalty: 0.35d,
        UnknownNodePenalty: 0.10d);

    public void Validate()
    {
        if (WarmupSamples is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(WarmupSamples));
        if (TtftTargetMilliseconds is < 1d or > 120_000d) throw new ArgumentOutOfRangeException(nameof(TtftTargetMilliseconds));
        ValidateWeight(TtftPenaltyWeight, nameof(TtftPenaltyWeight));
        ValidateWeight(FailurePenaltyWeight, nameof(FailurePenaltyWeight));
        ValidateWeight(ExternalLoadPenaltyWeight, nameof(ExternalLoadPenaltyWeight));
        ValidateWeight(QueuePenaltyWeight, nameof(QueuePenaltyWeight));
        if (KvCacheThreshold is < 0d or > 1d) throw new ArgumentOutOfRangeException(nameof(KvCacheThreshold));
        ValidateWeight(KvCachePenaltyWeight, nameof(KvCachePenaltyWeight));
        ValidateWeight(DegradedNodePenalty, nameof(DegradedNodePenalty));
        ValidateWeight(UnknownNodePenalty, nameof(UnknownNodePenalty));
    }

    private static void ValidateWeight(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value is < 0d or > 10d)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public sealed class RoutingTuningPolicy
{
    public const int SingletonId = 1;

    private RoutingTuningPolicy()
    {
    }

    public RoutingTuningPolicy(RoutingTuningSettings settings)
    {
        Id = SingletonId;
        Update(settings);
    }

    public int Id { get; private set; }
    public int WarmupSamples { get; private set; }
    public double TtftTargetMilliseconds { get; private set; }
    public double TtftPenaltyWeight { get; private set; }
    public double FailurePenaltyWeight { get; private set; }
    public double ExternalLoadPenaltyWeight { get; private set; }
    public double QueuePenaltyWeight { get; private set; }
    public double KvCacheThreshold { get; private set; }
    public double KvCachePenaltyWeight { get; private set; }
    public double DegradedNodePenalty { get; private set; }
    public double UnknownNodePenalty { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public RoutingTuningSettings ToSettings() => new(
        WarmupSamples,
        TtftTargetMilliseconds,
        TtftPenaltyWeight,
        FailurePenaltyWeight,
        ExternalLoadPenaltyWeight,
        QueuePenaltyWeight,
        KvCacheThreshold,
        KvCachePenaltyWeight,
        DegradedNodePenalty,
        UnknownNodePenalty);

    public void Update(RoutingTuningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        WarmupSamples = settings.WarmupSamples;
        TtftTargetMilliseconds = settings.TtftTargetMilliseconds;
        TtftPenaltyWeight = settings.TtftPenaltyWeight;
        FailurePenaltyWeight = settings.FailurePenaltyWeight;
        ExternalLoadPenaltyWeight = settings.ExternalLoadPenaltyWeight;
        QueuePenaltyWeight = settings.QueuePenaltyWeight;
        KvCacheThreshold = settings.KvCacheThreshold;
        KvCachePenaltyWeight = settings.KvCachePenaltyWeight;
        DegradedNodePenalty = settings.DegradedNodePenalty;
        UnknownNodePenalty = settings.UnknownNodePenalty;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
