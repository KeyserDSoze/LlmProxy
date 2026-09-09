namespace LlmProxy.Domain.Nodes;

public enum NodeStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    Draining = 4,
    Disabled = 5
}
