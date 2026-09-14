using System.Text.Json;
using System.Threading.Channels;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace LlmProxy.Infrastructure.Runtime;

public sealed class RedisRuntimeStateCoordinator : BackgroundService, IRuntimeStateEventSink
{
    private const string RouteCatalogKind = "route.catalog";
    private const string NodeKind = "route.node";
    private const string ModelKind = "route.model";
    private const string DeploymentKind = "route.deployment";
    private const string CredentialKind = "credential";
    private const string RatePolicyKind = "rate-policy";

    private readonly IRouteCatalog _routeCatalog;
    private readonly IApiCredentialCache _credentialCache;
    private readonly RequestRateLimiter _rateLimiter;
    private readonly ILogger<RedisRuntimeStateCoordinator> _logger;
    private readonly Channel<RuntimeStateMessage> _outbound = Channel.CreateUnbounded<RuntimeStateMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly string _connectionString;
    private readonly string _keyPrefix;
    private readonly string _instanceId;
    private readonly int _reconcileSeconds;
    private ConnectionMultiplexer? _connection;
    private long _lastAppliedVersion;
    private long _publishedEvents;
    private long _receivedEvents;
    private int _reconcileRequested;
    private volatile bool _connected;
    private string? _lastError;

    public RedisRuntimeStateCoordinator(
        IRouteCatalog routeCatalog,
        IApiCredentialCache credentialCache,
        RequestRateLimiter rateLimiter,
        IConfiguration configuration,
        ILogger<RedisRuntimeStateCoordinator> logger)
    {
        _routeCatalog = routeCatalog;
        _credentialCache = credentialCache;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _connectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Redis:ConnectionString is required when Redis synchronization is enabled.");
        _keyPrefix = NormalizePrefix(configuration["Redis:KeyPrefix"] ?? "llmproxy");
        _instanceId = configuration["Redis:InstanceId"]
            ?? Environment.GetEnvironmentVariable("HOSTNAME")
            ?? $"{Environment.MachineName}-{Guid.NewGuid():N}"[..Math.Min(Environment.MachineName.Length + 9, 40)];
        _reconcileSeconds = Math.Clamp(configuration.GetValue("Redis:ReconcileSeconds", 5), 1, 300);
    }

    public RuntimeStateSyncStatus GetStatus() => new(
        true,
        "redis-l2+local-l1",
        _instanceId,
        _connected && _connection?.IsConnected == true,
        Interlocked.Read(ref _lastAppliedVersion),
        Interlocked.Read(ref _publishedEvents),
        Interlocked.Read(ref _receivedEvents),
        _lastError);

    public void PublishRouteCatalogSnapshot(IEnumerable<RouteNodeSnapshot> nodes, IEnumerable<RouteModelSnapshot> models, IEnumerable<RouteDeploymentSnapshot> deployments)
        => Enqueue(RouteCatalogKind, "replace", null, new RouteCatalogPayload(nodes.ToArray(), models.ToArray(), deployments.ToArray()));

    public void PublishNodeUpsert(RouteNodeSnapshot node) => Enqueue(NodeKind, "upsert", node.Id, node);
    public void PublishNodeRemove(Guid nodeId) => Enqueue(NodeKind, "remove", nodeId, null);
    public void PublishModelUpsert(RouteModelSnapshot model) => Enqueue(ModelKind, "upsert", model.Id, model);
    public void PublishModelRemove(Guid modelId) => Enqueue(ModelKind, "remove", modelId, null);
    public void PublishDeploymentUpsert(RouteDeploymentSnapshot deployment) => Enqueue(DeploymentKind, "upsert", deployment.Id, deployment);
    public void PublishDeploymentRemove(Guid deploymentId) => Enqueue(DeploymentKind, "remove", deploymentId, null);
    public void PublishCredentialSnapshot(IEnumerable<ApiCredentialSnapshot> credentials) => Enqueue(CredentialKind, "replace", null, credentials.ToArray());
    public void PublishCredentialUpsert(ApiCredentialSnapshot credential) => Enqueue(CredentialKind, "upsert", credential.Id, credential);
    public void PublishCredentialRemove(Guid credentialId) => Enqueue(CredentialKind, "remove", credentialId, null);
    public void PublishRatePolicySnapshot(IEnumerable<RateLimitPolicySnapshot> policies) => Enqueue(RatePolicyKind, "replace", null, policies.ToArray());
    public void PublishRatePolicyUpsert(RateLimitPolicySnapshot policy) => Enqueue(RatePolicyKind, "upsert", policy.Id, policy);
    public void PublishRatePolicyRemove(Guid policyId) => Enqueue(RatePolicyKind, "remove", policyId, null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = ConfigurationOptions.Parse(_connectionString);
        options.AbortOnConnectFail = false;
        options.ClientName = $"llmproxy-{_instanceId}";

        try
        {
            _connection = await ConnectionMultiplexer.ConnectAsync(options);
            _connection.ConnectionFailed += (_, args) =>
            {
                _connected = false;
                _lastError = args.Exception?.Message ?? args.FailureType.ToString();
            };
            _connection.ConnectionRestored += (_, _) =>
            {
                _connected = true;
                _lastError = null;
                Interlocked.Exchange(ref _reconcileRequested, 1);
            };

            var subscriber = _connection.GetSubscriber();
            await subscriber.SubscribeAsync(RedisChannel.Literal(ChannelKey), (_, value) => ApplyIncoming(value));
            _connected = _connection.IsConnected;

            await Task.WhenAll(
                PublishLoopAsync(subscriber, stoppingToken),
                ReconcileLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _logger.LogError(exception, "Redis runtime-state coordinator stopped unexpectedly.");
        }
    }

    private async Task PublishLoopAsync(ISubscriber subscriber, CancellationToken cancellationToken)
    {
        await foreach (var message in _outbound.Reader.ReadAllAsync(cancellationToken))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var database = _connection!.GetDatabase();
                    await PersistAsync(database, message);
                    var version = await database.StringIncrementAsync(Key("version"));
                    var versioned = message with { Version = version };
                    await subscriber.PublishAsync(RedisChannel.Literal(ChannelKey), JsonSerializer.Serialize(versioned));
                    SetMax(ref _lastAppliedVersion, version);
                    Interlocked.Increment(ref _publishedEvents);
                    _lastError = null;
                    break;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _lastError = exception.Message;
                    _logger.LogWarning(exception, "Redis runtime-state publication failed; retrying.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_reconcileSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                if (_connection?.IsConnected != true)
                {
                    continue;
                }

                var database = _connection.GetDatabase();
                var versionValue = await database.StringGetAsync(Key("version"));
                if (!long.TryParse(versionValue.ToString(), out var redisVersion))
                {
                    continue;
                }

                var requested = Interlocked.Exchange(ref _reconcileRequested, 0) == 1;
                if (requested || redisVersion > Interlocked.Read(ref _lastAppliedVersion))
                {
                    await HydrateFromRedisAsync(database);
                    SetMax(ref _lastAppliedVersion, redisVersion);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _lastError = exception.Message;
                _logger.LogWarning(exception, "Redis runtime-state reconciliation failed.");
            }
        }
    }

    private void ApplyIncoming(RedisValue value)
    {
        try
        {
            var message = JsonSerializer.Deserialize<RuntimeStateMessage>(value.ToString());
            if (message is null)
            {
                return;
            }

            var previous = Interlocked.Read(ref _lastAppliedVersion);
            if (message.Version > previous + 1)
            {
                Interlocked.Exchange(ref _reconcileRequested, 1);
            }

            if (!string.Equals(message.OriginInstanceId, _instanceId, StringComparison.Ordinal))
            {
                ApplyToLocalState(message);
                Interlocked.Increment(ref _receivedEvents);
            }

            SetMax(ref _lastAppliedVersion, message.Version);
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            _logger.LogWarning(exception, "Ignored invalid Redis runtime-state event.");
            Interlocked.Exchange(ref _reconcileRequested, 1);
        }
    }

    private void ApplyToLocalState(RuntimeStateMessage message)
    {
        if (message.Kind == RouteCatalogKind && message.Action == "replace")
        {
            var payload = Deserialize<RouteCatalogPayload>(message.PayloadJson);
            _routeCatalog.Replace(payload.Nodes, payload.Models, payload.Deployments);
            return;
        }

        if (message.Kind == NodeKind)
        {
            if (message.Action == "remove") _routeCatalog.RemoveNode(message.EntityId!.Value);
            else _routeCatalog.Upsert(Deserialize<RouteNodeSnapshot>(message.PayloadJson));
            return;
        }

        if (message.Kind == ModelKind)
        {
            if (message.Action == "remove") _routeCatalog.RemoveModel(message.EntityId!.Value);
            else _routeCatalog.Upsert(Deserialize<RouteModelSnapshot>(message.PayloadJson));
            return;
        }

        if (message.Kind == DeploymentKind)
        {
            if (message.Action == "remove") _routeCatalog.RemoveDeployment(message.EntityId!.Value);
            else _routeCatalog.Upsert(Deserialize<RouteDeploymentSnapshot>(message.PayloadJson));
            return;
        }

        if (message.Kind == CredentialKind)
        {
            if (message.Action == "replace") _credentialCache.Replace(Deserialize<ApiCredentialSnapshot[]>(message.PayloadJson));
            else if (message.Action == "remove") _credentialCache.Remove(message.EntityId!.Value);
            else _credentialCache.Upsert(Deserialize<ApiCredentialSnapshot>(message.PayloadJson));
            return;
        }

        if (message.Kind == RatePolicyKind)
        {
            if (message.Action == "replace") _rateLimiter.ReplacePolicies(Deserialize<RateLimitPolicySnapshot[]>(message.PayloadJson));
            else if (message.Action == "remove") _rateLimiter.RemovePolicy(message.EntityId!.Value);
            else _rateLimiter.UpsertPolicy(Deserialize<RateLimitPolicySnapshot>(message.PayloadJson));
        }
    }

    private async Task PersistAsync(IDatabase database, RuntimeStateMessage message)
    {
        switch (message.Kind, message.Action)
        {
            case (RouteCatalogKind, "replace"):
            {
                var payload = Deserialize<RouteCatalogPayload>(message.PayloadJson);
                await ReplaceHashAsync(database, Key("route:nodes"), payload.Nodes, item => item.Id);
                await ReplaceHashAsync(database, Key("route:models"), payload.Models, item => item.Id);
                await ReplaceHashAsync(database, Key("route:deployments"), payload.Deployments, item => item.Id);
                await database.StringSetAsync(Key("route:seeded"), "1");
                break;
            }
            case (NodeKind, "upsert"):
                await UpsertHashAsync(database, Key("route:nodes"), message);
                break;
            case (NodeKind, "remove"):
                await database.HashDeleteAsync(Key("route:nodes"), IdField(message));
                break;
            case (ModelKind, "upsert"):
                await UpsertHashAsync(database, Key("route:models"), message);
                break;
            case (ModelKind, "remove"):
                await database.HashDeleteAsync(Key("route:models"), IdField(message));
                break;
            case (DeploymentKind, "upsert"):
                await UpsertHashAsync(database, Key("route:deployments"), message);
                break;
            case (DeploymentKind, "remove"):
                await database.HashDeleteAsync(Key("route:deployments"), IdField(message));
                break;
            case (CredentialKind, "replace"):
                await ReplaceHashAsync(database, Key("credentials"), Deserialize<ApiCredentialSnapshot[]>(message.PayloadJson), item => item.Id);
                await database.StringSetAsync(Key("credentials:seeded"), "1");
                break;
            case (CredentialKind, "upsert"):
                await UpsertHashAsync(database, Key("credentials"), message);
                break;
            case (CredentialKind, "remove"):
                await database.HashDeleteAsync(Key("credentials"), IdField(message));
                break;
            case (RatePolicyKind, "replace"):
                await ReplaceHashAsync(database, Key("rate-policies"), Deserialize<RateLimitPolicySnapshot[]>(message.PayloadJson), item => item.Id);
                await database.StringSetAsync(Key("rate-policies:seeded"), "1");
                break;
            case (RatePolicyKind, "upsert"):
                await UpsertHashAsync(database, Key("rate-policies"), message);
                break;
            case (RatePolicyKind, "remove"):
                await database.HashDeleteAsync(Key("rate-policies"), IdField(message));
                break;
        }
    }

    private async Task HydrateFromRedisAsync(IDatabase database)
    {
        if (await database.KeyExistsAsync(Key("route:seeded")))
        {
            var nodes = await ReadHashAsync<RouteNodeSnapshot>(database, Key("route:nodes"));
            var models = await ReadHashAsync<RouteModelSnapshot>(database, Key("route:models"));
            var deployments = await ReadHashAsync<RouteDeploymentSnapshot>(database, Key("route:deployments"));
            _routeCatalog.Replace(nodes, models, deployments);
        }

        if (await database.KeyExistsAsync(Key("credentials:seeded")))
        {
            _credentialCache.Replace(await ReadHashAsync<ApiCredentialSnapshot>(database, Key("credentials")));
        }

        if (await database.KeyExistsAsync(Key("rate-policies:seeded")))
        {
            _rateLimiter.ReplacePolicies(await ReadHashAsync<RateLimitPolicySnapshot>(database, Key("rate-policies")));
        }
    }

    private void Enqueue<T>(string kind, string action, Guid? entityId, T payload)
    {
        var payloadJson = payload is null ? null : JsonSerializer.Serialize(payload);
        _outbound.Writer.TryWrite(new RuntimeStateMessage(
            kind,
            action,
            entityId,
            payloadJson,
            _instanceId,
            DateTimeOffset.UtcNow,
            0));
    }

    private static async Task ReplaceHashAsync<T>(IDatabase database, RedisKey key, IEnumerable<T> values, Func<T, Guid> getId)
    {
        await database.KeyDeleteAsync(key);
        var entries = values
            .Select(value => new HashEntry(getId(value).ToString("N"), JsonSerializer.Serialize(value)))
            .ToArray();
        if (entries.Length > 0)
        {
            await database.HashSetAsync(key, entries);
        }
    }

    private static Task UpsertHashAsync(IDatabase database, RedisKey key, RuntimeStateMessage message)
        => database.HashSetAsync(key, IdField(message), message.PayloadJson ?? string.Empty);

    private static RedisValue IdField(RuntimeStateMessage message)
        => message.EntityId?.ToString("N") ?? throw new InvalidOperationException("Runtime-state entity id is required.");

    private static async Task<T[]> ReadHashAsync<T>(IDatabase database, RedisKey key)
    {
        var entries = await database.HashGetAllAsync(key);
        return entries
            .Select(entry => JsonSerializer.Deserialize<T>(entry.Value.ToString()))
            .Where(value => value is not null)
            .Cast<T>()
            .ToArray();
    }

    private static T Deserialize<T>(string? json)
        => !string.IsNullOrWhiteSpace(json)
            ? JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name} runtime state.")
            : throw new InvalidOperationException($"Missing {typeof(T).Name} runtime-state payload.");

    private static void SetMax(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private string Key(string suffix) => $"{_keyPrefix}:{suffix}";
    private string ChannelKey => Key("runtime-events");
    private static string NormalizePrefix(string value) => value.Trim().TrimEnd(':');

    public override void Dispose()
    {
        _connection?.Dispose();
        base.Dispose();
    }

    private sealed record RuntimeStateMessage(
        string Kind,
        string Action,
        Guid? EntityId,
        string? PayloadJson,
        string OriginInstanceId,
        DateTimeOffset OccurredAtUtc,
        long Version);

    private sealed record RouteCatalogPayload(
        RouteNodeSnapshot[] Nodes,
        RouteModelSnapshot[] Models,
        RouteDeploymentSnapshot[] Deployments);
}
