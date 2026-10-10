using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace LlmProxy.Infrastructure.Runtime;

public sealed class RedisCoordinationConnection : IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;
    private readonly string _keyPrefix;

    public RedisCoordinationConnection(IConfiguration configuration)
    {
        var connectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Redis:ConnectionString is required when Redis coordination is enabled.");

        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ClientName = $"llmproxy-coordination-{Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName}";

        _keyPrefix = NormalizePrefix(configuration["Redis:KeyPrefix"] ?? "llmproxy");
        _connection = new Lazy<Task<ConnectionMultiplexer>>(
            () => ConnectionMultiplexer.ConnectAsync(options),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connection.Value.WaitAsync(cancellationToken);
        return connection.GetDatabase();
    }

    public async Task<ISubscriber> GetSubscriberAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connection.Value.WaitAsync(cancellationToken);
        return connection.GetSubscriber();
    }

    public string Key(string suffix) => $"{_keyPrefix}:{suffix}";

    public async ValueTask DisposeAsync()
    {
        if (!_connection.IsValueCreated)
        {
            return;
        }

        try
        {
            var connection = await _connection.Value;
            connection.Dispose();
        }
        catch
        {
            // Connection creation may fail during shutdown. There is nothing left to dispose in that case.
        }
    }

    private static string NormalizePrefix(string prefix)
    {
        var normalized = prefix.Trim().Trim(':');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Redis:KeyPrefix cannot be empty when Redis coordination is enabled.");
        }

        return normalized;
    }
}
