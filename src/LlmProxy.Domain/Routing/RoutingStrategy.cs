namespace LlmProxy.Domain.Routing;

public enum RoutingStrategy
{
    WeightedLeastLoaded = 0,
    RoundRobin = 1,
    WeightedRoundRobin = 2
}
