namespace LlmProxy.Domain.Routing;

public sealed class RoutingPolicy
{
    public const int SingletonId = 1;

    private RoutingPolicy()
    {
    }

    public RoutingPolicy(RoutingStrategy strategy)
    {
        Id = SingletonId;
        SetStrategy(strategy);
    }

    public int Id { get; private set; } = SingletonId;
    public RoutingStrategy Strategy { get; private set; } = RoutingStrategy.WeightedLeastLoaded;
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void SetStrategy(RoutingStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported routing strategy.");
        }

        Strategy = strategy;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
