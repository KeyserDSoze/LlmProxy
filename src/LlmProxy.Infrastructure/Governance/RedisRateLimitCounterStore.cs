using LlmProxy.Application.Governance;
using LlmProxy.Infrastructure.Runtime;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LlmProxy.Infrastructure.Governance;

public sealed class RedisRateLimitCounterStore(
    RedisCoordinationConnection connection,
    InMemoryRateLimitCounterStore localFallback,
    ILogger<RedisRateLimitCounterStore> logger) : IRateLimitCounterStore
{
    private const string AcquireScript = """
        local now_parts = redis.call('TIME')
        local now_seconds = tonumber(now_parts[1])
        local request_limit = tonumber(ARGV[1])
        local window_seconds = tonumber(ARGV[2])
        local window_start = now_seconds - (now_seconds % window_seconds)
        local stored_start = tonumber(redis.call('HGET', KEYS[1], 'start') or '-1')

        if stored_start ~= window_start then
            redis.call('HSET', KEYS[1], 'start', window_start, 'count', 0)
            redis.call('EXPIRE', KEYS[1], window_seconds * 2)
        end

        local current_count = tonumber(redis.call('HGET', KEYS[1], 'count') or '0')
        if current_count >= request_limit then
            local retry_after = (window_start + window_seconds) - now_seconds
            if retry_after < 1 then
                retry_after = 1
            end
            return {0, retry_after, current_count}
        end

        current_count = redis.call('HINCRBY', KEYS[1], 'count', 1)
        return {1, 0, current_count}
        """;

    public async ValueTask<RateLimitCounterDecision> TryAcquireAsync(
        Guid policyId,
        int requestsPerWindow,
        int windowSeconds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1);

        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            var redisKey = (RedisKey)connection.Key($"rate-limit:{policyId:N}:{requestsPerWindow}:{windowSeconds}");
            var result = await database.ScriptEvaluateAsync(
                AcquireScript,
                [redisKey],
                [(RedisValue)requestsPerWindow, (RedisValue)windowSeconds]);

            var values = (RedisResult[])result!;
            var allowed = (long)values[0] == 1;
            var retryAfterSeconds = checked((int)(long)values[1]);
            var count = checked((int)(long)values[2]);

            return new RateLimitCounterDecision(
                allowed,
                retryAfterSeconds,
                count,
                "redis");
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogWarning(
                exception,
                "Redis rate-limit coordination failed for policy {PolicyId}; using local degraded enforcement.",
                policyId);

            var fallback = await localFallback.TryAcquireAsync(
                policyId,
                requestsPerWindow,
                windowSeconds,
                nowUtc,
                cancellationToken);

            return fallback with
            {
                Provider = "redis-fallback-local",
                Degraded = true
            };
        }
    }
}
