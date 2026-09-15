using LlmProxy.Application.Governance;
using LlmProxy.Infrastructure.Runtime;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LlmProxy.Infrastructure.Governance;

public sealed class RedisOutputTokenBudgetStore(
    RedisCoordinationConnection connection,
    ILogger<RedisOutputTokenBudgetStore> logger) : IOutputTokenBudgetStore
{
    private const string ReserveScript = """
        local now_parts = redis.call('TIME')
        local now_seconds = tonumber(now_parts[1])
        local budget = tonumber(ARGV[1])
        local window_seconds = tonumber(ARGV[2])
        local reservation = tonumber(ARGV[3])
        local window_start = now_seconds - (now_seconds % window_seconds)
        local stored_start = tonumber(redis.call('HGET', KEYS[1], 'start') or '-1')

        if stored_start ~= window_start then
            redis.call('HSET', KEYS[1], 'start', window_start, 'used', 0)
            redis.call('EXPIRE', KEYS[1], window_seconds * 2)
        end

        local used = tonumber(redis.call('HGET', KEYS[1], 'used') or '0')
        if used + reservation > budget then
            local retry_after = (window_start + window_seconds) - now_seconds
            if retry_after < 1 then retry_after = 1 end
            return {0, retry_after, used, window_start}
        end

        used = redis.call('HINCRBY', KEYS[1], 'used', reservation)
        return {1, 0, used, window_start}
        """;

    private const string SettleScript = """
        local expected_start = tonumber(ARGV[1])
        local refund = tonumber(ARGV[2])
        local stored_start = tonumber(redis.call('HGET', KEYS[1], 'start') or '-1')
        if stored_start ~= expected_start then
            return 0
        end

        if refund <= 0 then
            return tonumber(redis.call('HGET', KEYS[1], 'used') or '0')
        end

        local used = tonumber(redis.call('HGET', KEYS[1], 'used') or '0')
        local next_used = used - refund
        if next_used < 0 then next_used = 0 end
        redis.call('HSET', KEYS[1], 'used', next_used)
        return next_used
        """;

    public async ValueTask<OutputTokenBudgetStoreDecision> TryReserveAsync(
        Guid policyId,
        int outputTokensPerWindow,
        int windowSeconds,
        int reservationTokens,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(outputTokensPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(reservationTokens, 1);
        if (reservationTokens > outputTokensPerWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(reservationTokens), "Reservation cannot exceed the window budget.");
        }

        var key = (RedisKey)connection.Key($"output-token-budget:{policyId:N}:{outputTokensPerWindow}:{windowSeconds}");
        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            var result = await database.ScriptEvaluateAsync(
                ReserveScript,
                [key],
                [(RedisValue)outputTokensPerWindow, (RedisValue)windowSeconds, (RedisValue)reservationTokens]);

            var values = (RedisResult[])result!;
            var acquired = (long)values[0] == 1;
            var retryAfterSeconds = checked((int)(long)values[1]);
            var windowUsage = (long)values[2];
            var windowStartUnixSeconds = (long)values[3];

            if (!acquired)
            {
                return OutputTokenBudgetStoreDecision.Reject(retryAfterSeconds, windowUsage, "redis");
            }

            return OutputTokenBudgetStoreDecision.Permit(
                windowUsage,
                "redis",
                new RedisReservation(
                    connection,
                    logger,
                    key,
                    policyId,
                    windowStartUnixSeconds,
                    reservationTokens));
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogWarning(
                exception,
                "Redis output-token budget coordination failed for policy {PolicyId}; admission fails closed.",
                policyId);
            return OutputTokenBudgetStoreDecision.Unavailable("redis");
        }
    }

    private sealed class RedisReservation(
        RedisCoordinationConnection connection,
        ILogger logger,
        RedisKey key,
        Guid policyId,
        long windowStartUnixSeconds,
        int reservedTokens) : IOutputTokenBudgetReservation
    {
        private int _settled;

        public int ReservedTokens { get; } = reservedTokens;

        public async ValueTask SettleAsync(
            int? actualOutputTokens,
            bool usageCertain,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0 ||
                !usageCertain ||
                actualOutputTokens is not int actual ||
                actual < 0 ||
                actual > ReservedTokens)
            {
                return;
            }

            var refund = ReservedTokens - actual;
            if (refund <= 0)
            {
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var database = await connection.GetDatabaseAsync(cancellationToken);
                await database.ScriptEvaluateAsync(
                    SettleScript,
                    [key],
                    [(RedisValue)windowStartUnixSeconds, (RedisValue)refund]);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                exception is RedisException or TimeoutException)
            {
                // Conservative failure mode: the original full reservation remains charged.
                logger.LogWarning(
                    exception,
                    "Redis output-token budget settlement failed for policy {PolicyId}; keeping the full {ReservedTokens}-token reservation charged.",
                    policyId,
                    ReservedTokens);
            }
        }
    }
}
