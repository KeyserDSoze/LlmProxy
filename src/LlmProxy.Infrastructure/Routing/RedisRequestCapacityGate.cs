using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Observability;
using LlmProxy.Infrastructure.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LlmProxy.Infrastructure.Routing;

public sealed class RedisRequestCapacityGate(
    RedisCoordinationConnection connection,
    IRequestLoadTracker localLoadTracker,
    IConfiguration configuration,
    ILogger<RedisRequestCapacityGate> logger) : IRequestCapacityGate
{
    private const string AcquireScript = """
        local time_parts = redis.call('TIME')
        local now_ms = (tonumber(time_parts[1]) * 1000) + math.floor(tonumber(time_parts[2]) / 1000)
        local deployment_limit = tonumber(ARGV[2])
        local node_limit = tonumber(ARGV[3])
        local ttl_ms = tonumber(ARGV[4])
        local key_ttl_seconds = tonumber(ARGV[5])

        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now_ms)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now_ms)

        local deployment_active = tonumber(redis.call('ZCARD', KEYS[1]))
        local node_active = tonumber(redis.call('ZCARD', KEYS[2]))

        if redis.call('HEXISTS', KEYS[3], ARGV[6]) == 1 or redis.call('EXISTS', KEYS[4]) == 1 then
            return {0, deployment_active, node_active, 3}
        end
        if deployment_active >= deployment_limit then
            return {0, deployment_active, node_active, 1}
        end
        if node_active >= node_limit then
            return {0, deployment_active, node_active, 2}
        end

        local expiry_ms = now_ms + ttl_ms
        redis.call('ZADD', KEYS[1], expiry_ms, ARGV[1])
        redis.call('ZADD', KEYS[2], expiry_ms, ARGV[1])
        redis.call('EXPIRE', KEYS[1], key_ttl_seconds)
        redis.call('EXPIRE', KEYS[2], key_ttl_seconds)
        return {1, deployment_active + 1, node_active + 1, 0}
        """;

    private const string RenewScript = """
        if not redis.call('ZSCORE', KEYS[1], ARGV[1]) or not redis.call('ZSCORE', KEYS[2], ARGV[1]) then
            return 0
        end
        local time_parts = redis.call('TIME')
        local now_ms = (tonumber(time_parts[1]) * 1000) + math.floor(tonumber(time_parts[2]) / 1000)
        local expiry_ms = now_ms + tonumber(ARGV[2])
        redis.call('ZADD', KEYS[1], 'XX', expiry_ms, ARGV[1])
        redis.call('ZADD', KEYS[2], 'XX', expiry_ms, ARGV[1])
        redis.call('EXPIRE', KEYS[1], tonumber(ARGV[3]))
        redis.call('EXPIRE', KEYS[2], tonumber(ARGV[3]))
        return 1
        """;

    private const string ReleaseScript = """
        redis.call('ZREM', KEYS[1], ARGV[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        return 1
        """;

    private readonly int _leaseSeconds = Math.Clamp(configuration.GetValue("Redis:CapacityLeaseSeconds", 120), 10, 3600);
    private readonly int _renewSeconds = NormalizeRenewSeconds(
        configuration.GetValue("Redis:CapacityRenewSeconds", 30),
        Math.Clamp(configuration.GetValue("Redis:CapacityLeaseSeconds", 120), 10, 3600));

    public async ValueTask<CapacityAdmissionResult> TryAcquireAsync(
        Guid deploymentId,
        Guid nodeId,
        int deploymentMaxConcurrency,
        int nodeMaxConcurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deploymentMaxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeMaxConcurrency, 1);

        using var activity = LlmProxyActivity.Start("llmproxy.capacity.acquire");
        activity?.SetTag("llmproxy.capacity.provider", "redis");
        LlmProxyActivity.SetGuid(activity, "llmproxy.deployment.id", deploymentId);
        LlmProxyActivity.SetGuid(activity, "llmproxy.node.id", nodeId);
        activity?.SetTag("llmproxy.capacity.deployment_limit", deploymentMaxConcurrency);
        activity?.SetTag("llmproxy.capacity.node_limit", nodeMaxConcurrency);
        activity?.SetTag("llmproxy.capacity.lease_seconds", _leaseSeconds);
        activity?.SetTag("llmproxy.capacity.renew_seconds", _renewSeconds);

        var leaseId = Guid.NewGuid().ToString("N");
        var deploymentKey = (RedisKey)connection.Key($"capacity:deployment:{deploymentId:N}");
        var nodeKey = (RedisKey)connection.Key($"capacity:node:{nodeId:N}");
        var maintenanceKey = (RedisKey)connection.Key("maintenance:nodes");
        var pendingMaintenanceKey = (RedisKey)connection.Key($"maintenance:predrain:{nodeId:N}");
        var keyTtlSeconds = _leaseSeconds * 2;

        try
        {
            var database = await connection.GetDatabaseAsync(cancellationToken);
            var result = await database.ScriptEvaluateAsync(
                AcquireScript,
                [deploymentKey, nodeKey, maintenanceKey, pendingMaintenanceKey],
                [
                    (RedisValue)leaseId,
                    (RedisValue)deploymentMaxConcurrency,
                    (RedisValue)nodeMaxConcurrency,
                    (RedisValue)(_leaseSeconds * 1000L),
                    (RedisValue)keyTtlSeconds,
                    (RedisValue)nodeId.ToString("N")
                ]);

            var values = (RedisResult[])result!;
            var acquired = (long)values[0] == 1;
            var deploymentActive = (long)values[1];
            var nodeActive = (long)values[2];
            var rejectionCode = (long)values[3];

            activity?.SetTag("llmproxy.capacity.deployment_active_after", deploymentActive);
            activity?.SetTag("llmproxy.capacity.node_active_after", nodeActive);

            if (!acquired)
            {
                var scope = rejectionCode switch
                {
                    1 => "deployment",
                    2 => "node",
                    3 => "node_maintenance",
                    _ => "unknown"
                };
                activity?.SetTag("llmproxy.capacity.result", rejectionCode == 3 ? "node_maintenance" : "rejected");
                activity?.SetTag("llmproxy.capacity.rejection_scope", scope);
                LlmProxyActivity.MarkError(activity, rejectionCode == 3 ? "node_maintenance" : "capacity_exhausted");
                return CapacityAdmissionResult.Rejected("redis", scope);
            }

            if (!localLoadTracker.TryEnter(
                    deploymentId,
                    nodeId,
                    deploymentMaxConcurrency,
                    nodeMaxConcurrency,
                    out var localLease) || localLease is null)
            {
                await ReleaseAsync(database, deploymentKey, nodeKey, leaseId);
                activity?.SetTag("llmproxy.capacity.result", "local_rejected_after_redis");
                LlmProxyActivity.MarkError(activity, "capacity_exhausted");
                return CapacityAdmissionResult.Rejected("redis", "local");
            }

            activity?.SetTag("llmproxy.capacity.result", "acquired");
            return CapacityAdmissionResult.Success(
                new RedisCapacityLease(
                    connection,
                    localLease,
                    deploymentKey,
                    nodeKey,
                    leaseId,
                    _leaseSeconds,
                    _renewSeconds,
                    logger),
                "redis");
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested &&
            exception is RedisException or TimeoutException)
        {
            logger.LogError(
                exception,
                "Redis capacity coordination is unavailable for deployment {DeploymentId} on node {NodeId}; failing closed.",
                deploymentId,
                nodeId);
            activity?.SetTag("llmproxy.capacity.result", "coordination_unavailable");
            LlmProxyActivity.MarkError(activity, "capacity_coordination_unavailable");
            return CapacityAdmissionResult.Unavailable("redis");
        }
    }

    private static async Task ReleaseAsync(IDatabase database, RedisKey deploymentKey, RedisKey nodeKey, string leaseId)
    {
        await database.ScriptEvaluateAsync(
            ReleaseScript,
            [deploymentKey, nodeKey],
            [(RedisValue)leaseId]);
    }

    private static int NormalizeRenewSeconds(int configured, int leaseSeconds)
        => Math.Clamp(configured, 1, Math.Max(1, leaseSeconds / 2));

    private sealed class RedisCapacityLease : IRequestCapacityLease
    {
        private readonly RedisCoordinationConnection _connection;
        private readonly IDisposable _localLease;
        private readonly RedisKey _deploymentKey;
        private readonly RedisKey _nodeKey;
        private readonly string _leaseId;
        private readonly int _leaseSeconds;
        private readonly int _renewSeconds;
        private readonly CapacityLeaseValidityTracker _validityTracker;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly CancellationTokenSource _coordinationLost = new();
        private readonly Task _renewTask;
        private readonly Task _safetyTask;
        private int _lossSignaled;
        private int _disposed;

        public RedisCapacityLease(
            RedisCoordinationConnection connection,
            IDisposable localLease,
            RedisKey deploymentKey,
            RedisKey nodeKey,
            string leaseId,
            int leaseSeconds,
            int renewSeconds,
            ILogger logger)
        {
            _connection = connection;
            _localLease = localLease;
            _deploymentKey = deploymentKey;
            _nodeKey = nodeKey;
            _leaseId = leaseId;
            _leaseSeconds = leaseSeconds;
            _renewSeconds = renewSeconds;
            _validityTracker = new CapacityLeaseValidityTracker(
                TimeProvider.System,
                TimeSpan.FromSeconds(Math.Max(1, leaseSeconds - renewSeconds)));
            _logger = logger;
            _renewTask = RenewLoopAsync(_lifetimeCancellation.Token);
            _safetyTask = SafetyLoopAsync(_lifetimeCancellation.Token);
        }

        public CancellationToken CoordinationLost => _coordinationLost.Token;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _lifetimeCancellation.Cancel();
            try
            {
                await Task.WhenAll(_renewTask, _safetyTask);
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                var database = await _connection.GetDatabaseAsync();
                await ReleaseAsync(database, _deploymentKey, _nodeKey, _leaseId);
            }
            catch (Exception exception) when (exception is RedisException or TimeoutException)
            {
                _logger.LogWarning(exception, "Could not release Redis capacity lease {LeaseId}; expiry will recover it.", _leaseId);
            }
            finally
            {
                _localLease.Dispose();
                _lifetimeCancellation.Dispose();
                _coordinationLost.Dispose();
            }
        }

        private async Task RenewLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_renewSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var database = await _connection.GetDatabaseAsync(cancellationToken);
                    var result = await database.ScriptEvaluateAsync(
                        RenewScript,
                        [_deploymentKey, _nodeKey],
                        [
                            (RedisValue)_leaseId,
                            (RedisValue)(_leaseSeconds * 1000L),
                            (RedisValue)(_leaseSeconds * 2)
                        ]);

                    if ((long)result != 1)
                    {
                        SignalCoordinationLost("the Redis lease was no longer present during renewal");
                        return;
                    }

                    _validityTracker.MarkRenewed();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is RedisException or TimeoutException)
                {
                    _logger.LogWarning(exception, "Redis capacity lease {LeaseId} renewal failed; safety watchdog remains armed.", _leaseId);
                }
            }
        }

        private async Task SafetyLoopAsync(CancellationToken cancellationToken)
        {
            var pollInterval = TimeSpan.FromSeconds(Math.Min(1d, _renewSeconds / 2d));
            using var timer = new PeriodicTimer(pollInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_validityTracker.HasCrossedSafetyDeadline())
                {
                    continue;
                }

                var elapsed = _validityTracker.GetElapsedSinceLastRenewal();
                SignalCoordinationLost(
                    $"the lease has not been renewed for {elapsed.TotalSeconds:F1}s and is approaching its {_leaseSeconds}s Redis expiry");
                return;
            }
        }

        private void SignalCoordinationLost(string reason)
        {
            if (Interlocked.Exchange(ref _lossSignaled, 1) != 0)
            {
                return;
            }

            _logger.LogError(
                "Redis capacity lease {LeaseId} lost safe coordination because {Reason}; active inference must be cancelled before lease expiry.",
                _leaseId,
                reason);
            _coordinationLost.Cancel();
        }
    }
}
