using System.Threading.Channels;
using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmProxy.Infrastructure.Security;

public sealed class BufferedCredentialUsageSink(
    IServiceScopeFactory scopeFactory,
    ILogger<BufferedCredentialUsageSink> logger) : BackgroundService, ICredentialUsageSink
{
    private static readonly TimeSpan PersistenceInterval = TimeSpan.FromMinutes(15);

    private sealed record UsageEvent(Guid CredentialId, DateTimeOffset UsedAtUtc);

    private readonly Channel<UsageEvent> _channel = Channel.CreateBounded<UsageEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _throttleGate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _nextEligibleUtc = new();

    public void RecordUsage(Guid credentialId, DateTimeOffset usedAtUtc)
    {
        lock (_throttleGate)
        {
            if (_nextEligibleUtc.TryGetValue(credentialId, out var nextEligible) && usedAtUtc < nextEligible)
            {
                return;
            }

            _nextEligibleUtc[credentialId] = usedAtUtc + PersistenceInterval;
        }

        if (!_channel.Writer.TryWrite(new UsageEvent(credentialId, usedAtUtc)))
        {
            lock (_throttleGate)
            {
                _nextEligibleUtc.Remove(credentialId);
            }

            logger.LogWarning("Last-used update for API credential {CredentialId} could not be queued.", credentialId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _channel.Reader.WaitToReadAsync(stoppingToken))
        {
            try
            {
                // Give concurrent requests a short window to collapse into one database batch.
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var latestByCredential = new Dictionary<Guid, DateTimeOffset>();
            while (latestByCredential.Count < 200 && _channel.Reader.TryRead(out var usage))
            {
                if (!latestByCredential.TryGetValue(usage.CredentialId, out var current) || usage.UsedAtUtc > current)
                {
                    latestByCredential[usage.CredentialId] = usage.UsedAtUtc;
                }
            }

            if (latestByCredential.Count == 0)
            {
                continue;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                var ids = latestByCredential.Keys.ToArray();
                var credentials = await dbContext.ApiCredentials
                    .Where(credential => ids.Contains(credential.Id))
                    .ToListAsync(stoppingToken);

                foreach (var credential in credentials)
                {
                    credential.Touch(latestByCredential[credential.Id]);
                }

                await dbContext.SaveChangesAsync(stoppingToken);

                lock (_throttleGate)
                {
                    foreach (var credential in credentials)
                    {
                        var persistedAt = credential.LastUsedAtUtc ?? latestByCredential[credential.Id];
                        _nextEligibleUtc[credential.Id] = persistedAt + PersistenceInterval;
                    }

                    foreach (var missingId in ids.Except(credentials.Select(credential => credential.Id)))
                    {
                        _nextEligibleUtc.Remove(missingId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                lock (_throttleGate)
                {
                    foreach (var credentialId in latestByCredential.Keys)
                    {
                        _nextEligibleUtc.Remove(credentialId);
                    }
                }

                logger.LogError(exception, "Failed to persist API credential last-used metadata for {CredentialCount} credentials.", latestByCredential.Count);
            }
        }
    }
}
