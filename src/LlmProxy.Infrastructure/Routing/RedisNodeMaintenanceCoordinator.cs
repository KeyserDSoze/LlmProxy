using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LlmProxy.Infrastructure.Routing;

public sealed class RedisNodeMaintenanceCoordinator(
    RedisCoordinationConnection connection,
    IConfiguration configuration,
    ILogger<RedisNodeMaintenanceCoordinator> logger) : INodeMaintenanceCoordinator
{
    private const string StatusScript = """
        local time_parts = redis.call('TIME')
        local now_ms = (tonumber(time_parts[1]) * 1000) + math.floor(tonumber(time_parts[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now_ms)
        local active = tonumber(redis.call('ZCARD', KEYS[1]))
        local durable = tonumber(redis.call('HEXISTS', KEYS[2], ARGV[1]))
        local pending = tonumber(redis.call('EXISTS', KEYS[3]))
        if durable == 1 or pending == 1 then
            return {1, active}
        end
        return {0, active}
        """;

    private readonly int _pendingDrainSeconds = Math.Clamp(
        configuration.GetValue("Redis:MaintenancePendingDrainSeconds", 300),
        30,
        3600);

    public async ValueTask<bool> TryBeginDrainAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            return await database.StringSetAsync(
                PendingKey(nodeId),
                "1",
                TimeSpan.FromSeconds(_pendingDrainSeconds));
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogError(exception, "Redis node-maintenance coordination is unavailable while beginning drain for {NodeId}.", nodeId);
            return false;
        }
    }

    public async ValueTask<bool> TryConfirmDrainAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            await database.HashSetAsync(DurableKey, nodeId.ToString("N"), "draining");
            await database.KeyDeleteAsync(PendingKey(nodeId));
            return true;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogWarning(exception, "Could not promote the pending drain marker for {NodeId}; the persisted Draining state remains authoritative.", nodeId);
            return false;
        }
    }

    public async ValueTask CancelPendingDrainAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            await database.KeyDeleteAsync(PendingKey(nodeId));
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogWarning(exception, "Could not clear pending drain marker for {NodeId}; its TTL will recover it.", nodeId);
        }
    }

    public async ValueTask<NodeMaintenanceStatus> GetStatusAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            var result = await database.ScriptEvaluateAsync(
                StatusScript,
                [NodeCapacityKey(nodeId), DurableKey, PendingKey(nodeId)],
                [(RedisValue)nodeId.ToString("N")]);
            var values = (RedisResult[])result!;
            return new NodeMaintenanceStatus(
                CoordinationAvailable: true,
                AdmissionBlocked: (long)values[0] == 1,
                ActiveRequests: (long)values[1],
                Provider: "redis");
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogError(exception, "Redis node-maintenance status is unavailable for {NodeId}.", nodeId);
            return new NodeMaintenanceStatus(
                CoordinationAvailable: false,
                AdmissionBlocked: true,
                ActiveRequests: -1,
                Provider: "redis",
                Error: exception.Message);
        }
    }

    public async ValueTask<bool> TryResumeAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            await database.HashDeleteAsync(DurableKey, nodeId.ToString("N"));
            await database.KeyDeleteAsync(PendingKey(nodeId));
            return true;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogWarning(exception, "Could not clear node-maintenance admission block for {NodeId}.", nodeId);
            return false;
        }
    }

    private RedisKey DurableKey => connection.Key("maintenance:nodes");
    private RedisKey PendingKey(Guid nodeId) => connection.Key($"maintenance:predrain:{nodeId:N}");
    private RedisKey NodeCapacityKey(Guid nodeId) => connection.Key($"capacity:node:{nodeId:N}");
}
