using LlmProxy.Domain.Routing;

namespace LlmProxy.Application.Routing;

public sealed class RoutingStrategyState(RoutingStrategy initialStrategy)
{
    private int _current = (int)initialStrategy;

    public RoutingStrategy Current => (RoutingStrategy)Volatile.Read(ref _current);

    public void Set(RoutingStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unsupported routing strategy.");
        }

        Volatile.Write(ref _current, (int)strategy);
    }
}
