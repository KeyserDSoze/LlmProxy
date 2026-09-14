using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class InMemoryApiCredentialCacheTests
{
    [Fact]
    public void ReplacePublishesUsableCredentialByHash()
    {
        var cache = new InMemoryApiCredentialCache();
        var credential = Snapshot(enabled: true);

        cache.Replace([credential]);

        Assert.True(cache.TryGetUsableByHash(credential.KeyHash, DateTimeOffset.UtcNow, out var resolved));
        Assert.Equal(credential.Id, resolved.Id);
        Assert.Equal(credential.UsageGroupId, resolved.UsageGroupId);
    }

    [Fact]
    public void RevokedAndExpiredSnapshotsAreRejectedWithoutRemovingThemFromCache()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new InMemoryApiCredentialCache();
        var revoked = Snapshot(keyHash: "revoked", enabled: false);
        var expired = Snapshot(keyHash: "expired", enabled: true, expiresAtUtc: now.AddSeconds(-1));
        cache.Replace([revoked, expired]);

        Assert.False(cache.TryGetUsableByHash("revoked", now, out _));
        Assert.False(cache.TryGetUsableByHash("expired", now, out _));
    }

    [Fact]
    public void UpsertAtomicallyPublishesGroupAndRevocationChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new InMemoryApiCredentialCache();
        var credential = Snapshot(enabled: true, usageGroupId: null);
        cache.Replace([credential]);

        var groupId = Guid.NewGuid();
        cache.Upsert(credential with { UsageGroupId = groupId });
        Assert.True(cache.TryGetUsableByHash(credential.KeyHash, now, out var grouped));
        Assert.Equal(groupId, grouped.UsageGroupId);

        cache.Upsert(grouped with { Enabled = false });
        Assert.False(cache.TryGetUsableByHash(credential.KeyHash, now, out _));
    }

    [Fact]
    public void ReplaceDropsStaleCredentialsAndConcurrentReadsSeePublishedSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new InMemoryApiCredentialCache();
        var first = Snapshot(keyHash: "first", enabled: true);
        var second = Snapshot(keyHash: "second", enabled: true);
        cache.Replace([first]);
        cache.Replace([second]);

        Assert.False(cache.TryGetUsableByHash("first", now, out _));
        Assert.True(cache.TryGetUsableByHash("second", now, out _));

        var failures = 0;
        Parallel.For(0, 1000, index =>
        {
            cache.Upsert(second with { UsageGroupId = index % 2 == 0 ? Guid.Empty : null });
            if (!cache.TryGetUsableByHash("second", now, out _))
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
    }

    private static ApiCredentialSnapshot Snapshot(
        string? keyHash = null,
        bool enabled = true,
        DateTimeOffset? expiresAtUtc = null,
        Guid? usageGroupId = null)
        => new(
            Guid.NewGuid(),
            keyHash ?? Guid.NewGuid().ToString("N"),
            enabled,
            expiresAtUtc,
            usageGroupId);
}
