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
        local rejected_index = 0
        local rejected_count = 0
        local max_retry_after = 0

        for i = 1, #KEYS do
            local request_limit = tonumber(ARGV[((i - 1) * 2) + 1])
            local window_seconds = tonumber(ARGV[((i - 1) * 2) + 2])
            local window_start = now_seconds - (now_seconds % window_seconds)
            local stored_start = tonumber(redis.call('HGET', KEYS[i], 'start') or '-1')

            if stored_start ~= window_start then
                redis.call('HSET', KEYS[i], 'start', window_start, 'count', 0)
                redis.call('EXPIRE', KEYS[i], window_seconds * 2)
            end

            local current_count = tonumber(redis.call('HGET', KEYS[i], 'count') or '0')
            if current_count >= request_limit then
                local retry_after = (window_start + window_seconds) - now_seconds
                if retry_after < 1 then
                    retry_after = 1
                end
                if rejected_index == 0 or retry_after > max_retry_after then
                    rejected_index = i
                    rejected_count = current_count
                    max_retry_after = retry_after
                end
            end
        end

        if rejected_index ~= 0 then
            return {0, max_retry_after, rejected_index, rejected_count}
        end

        local max_count = 0
        for i = 1, #KEYS do
            local current_count = redis.call('HINCRBY', KEYS[i], 'count', 1)
            if current_count > max_count then
                max_count = current_count
            end
        end

        return {1, 0, 0, max_count}
        """;

    public async ValueTask<RateLimitCounterDecision> TryAcquireAsync(
        IReadOnlyList<RateLimitCounterRequest> requests,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count == 0)
        {
            return new RateLimitCounterDecision(true, 0, 0, "redis");
        }

        foreach (var request in requests)
        {
            if (request.PolicyId == Guid.Empty)
            {
                throw new ArgumentException("Policy id is required.", nameof(requests));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(request.RequestsPerWindow, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(request.WindowSeconds, 1);
        }

        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            var keys = requests
                .Select(request => (RedisKey)connection.Key(
                    $"rate-limit:{request.PolicyId:N}:{request.RequestsPerWindow}:{request.WindowSeconds}"))
                .ToArray();
            var args = requests
                .SelectMany(request => new RedisValue[]
                {
                    request.RequestsPerWindow,
                    request.WindowSeconds
                })
                .ToArray();

            var result = await database.ScriptEvaluateAsync(AcquireScript, keys, args);
            var values = (RedisResult[])result!;
            var allowed = (long)values[0] == 1;
            var retryAfterSeconds = checked((int)(long)values[1]);
            var rejectedIndex = checked((int)(long)values[2]);
            var count = checked((int)(long)values[3]);
            Guid? rejectedPolicyId = rejectedIndex > 0 && rejectedIndex <= requests.Count
                ? requests[rejectedIndex - 1].PolicyId
                : null;

            return new RateLimitCounterDecision(
                allowed,
                retryAfterSeconds,
                count,
                "redis",
                RejectedPolicyId: rejectedPolicyId);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogWarning(
                exception,
                "Redis rate-limit coordination failed for {PolicyCount} policies; using local degraded enforcement.",
                requests.Count);

            var fallback = await localFallback.TryAcquireAsync(
                requests,
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
