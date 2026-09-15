namespace LlmProxy.Infrastructure.Runtime;

public sealed record RuntimeStateChange(
    string Kind,
    string Action,
    Guid? EntityId,
    string? PayloadJson,
    DateTimeOffset OccurredAtUtc);

public interface IRuntimeStateDurablePublisher
{
    Task PublishAsync(RuntimeStateChange change, CancellationToken cancellationToken = default);
}

internal static class RuntimeStateChangeKinds
{
    public const string RouteCatalog = "route.catalog";
    public const string Node = "route.node";
    public const string Model = "route.model";
    public const string Deployment = "route.deployment";
    public const string Credential = "credential";
    public const string RatePolicy = "rate-policy";

    public const string Replace = "replace";
    public const string Upsert = "upsert";
    public const string Remove = "remove";
}