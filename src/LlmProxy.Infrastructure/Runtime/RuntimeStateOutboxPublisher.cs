using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;

namespace LlmProxy.Infrastructure.Runtime;

public interface IRuntimeStateTransportPublisher
{
    Task PublishAsync(RuntimeStateChange change, CancellationToken cancellationToken = default);
}

public sealed class RedisRuntimeStateTransportPublisher(RedisRuntimeStateCoordinator coordinator)
    : IRuntimeStateTransportPublisher
{
    public Task PublishAsync(RuntimeStateChange change, CancellationToken cancellationToken = default)
        => coordinator.PublishAsync(change, cancellationToken);
}

public sealed class RuntimeStateOutboxPublisher(
    IRuntimeStateTransportPublisher transport,
    IRouteCatalog routeCatalog,
    IApiCredentialCache credentialCache,
    RequestRateLimiter rateLimiter) : IRuntimeStateDurablePublisher
{
    public async Task PublishAsync(RuntimeStateChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        // Materialize and validate the local mutation before publishing. A malformed durable record must
        // not advance the Redis version or emit an event that other replicas cannot consume.
        var applyLocally = BuildLocalApply(change);

        await transport.PublishAsync(change, cancellationToken);

        // The replica that wins the PostgreSQL advisory lock may not be the replica that originated the
        // database mutation. Redis pub/sub ignores events emitted by the current instance, so the durable
        // publisher must apply the acknowledged mutation to its own L1 as well.
        applyLocally();
    }

    private Action BuildLocalApply(RuntimeStateChange change)
    {
        return (change.Kind, change.Action) switch
        {
            (RuntimeStateChangeKinds.Node, RuntimeStateChangeKinds.Upsert) =>
                CapturePayload<RouteNodeSnapshot>(change.PayloadJson, routeCatalog.Upsert),
            (RuntimeStateChangeKinds.Node, RuntimeStateChangeKinds.Remove) =>
                CaptureEntityId(change, routeCatalog.RemoveNode),

            (RuntimeStateChangeKinds.Model, RuntimeStateChangeKinds.Upsert) =>
                CapturePayload<RouteModelSnapshot>(change.PayloadJson, routeCatalog.Upsert),
            (RuntimeStateChangeKinds.Model, RuntimeStateChangeKinds.Remove) =>
                CaptureEntityId(change, routeCatalog.RemoveModel),

            (RuntimeStateChangeKinds.Deployment, RuntimeStateChangeKinds.Upsert) =>
                CapturePayload<RouteDeploymentSnapshot>(change.PayloadJson, routeCatalog.Upsert),
            (RuntimeStateChangeKinds.Deployment, RuntimeStateChangeKinds.Remove) =>
                CaptureEntityId(change, routeCatalog.RemoveDeployment),

            (RuntimeStateChangeKinds.Credential, RuntimeStateChangeKinds.Upsert) =>
                CapturePayload<ApiCredentialSnapshot>(change.PayloadJson, credentialCache.Upsert),
            (RuntimeStateChangeKinds.Credential, RuntimeStateChangeKinds.Remove) =>
                CaptureEntityId(change, credentialCache.Remove),

            (RuntimeStateChangeKinds.RatePolicy, RuntimeStateChangeKinds.Upsert) =>
                CapturePayload<RateLimitPolicySnapshot>(change.PayloadJson, rateLimiter.UpsertPolicy),
            (RuntimeStateChangeKinds.RatePolicy, RuntimeStateChangeKinds.Remove) =>
                CaptureEntityId(change, rateLimiter.RemovePolicy),

            _ => throw new InvalidOperationException(
                $"Unsupported transactional runtime-state change {change.Kind}/{change.Action}.")
        };
    }

    private static Action CapturePayload<T>(string? json, Action<T> apply)
    {
        var value = Deserialize<T>(json);
        return () => apply(value);
    }

    private static Action CaptureEntityId(RuntimeStateChange change, Action<Guid> apply)
    {
        var entityId = RequireEntityId(change);
        return () => apply(entityId);
    }

    private static Guid RequireEntityId(RuntimeStateChange change)
        => change.EntityId is Guid entityId && entityId != Guid.Empty
            ? entityId
            : throw new InvalidOperationException(
                $"Runtime-state change {change.Kind}/{change.Action} requires an entity id.");

    private static T Deserialize<T>(string? json)
        => !string.IsNullOrWhiteSpace(json)
            ? JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name} runtime-state payload.")
            : throw new InvalidOperationException($"Missing {typeof(T).Name} runtime-state payload.");
}
