using LlmProxy.Application.Governance;

namespace LlmProxy.UnitTests.Application;

public sealed class RequestRateLimiterTests
{
    [Fact]
    public void Default_policy_rejects_after_limit_and_resets_after_window()
    {
        var credentialId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var limiter = new RequestRateLimiter();
        limiter.ReplacePolicies([
            new RateLimitPolicySnapshot(policyId, credentialId, null, 2, 60, true)
        ]);
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(credentialId, "agic-code", now).Allowed);
        Assert.True(limiter.TryAcquire(credentialId, "agic-code", now.AddSeconds(1)).Allowed);

        var rejected = limiter.TryAcquire(credentialId, "agic-code", now.AddSeconds(2));
        Assert.False(rejected.Allowed);
        Assert.Equal(policyId, rejected.Policy?.Id);
        Assert.InRange(rejected.RetryAfterSeconds, 1, 58);

        Assert.True(limiter.TryAcquire(credentialId, "agic-code", now.AddSeconds(61)).Allowed);
    }

    [Fact]
    public void Model_override_wins_over_credential_default()
    {
        var credentialId = Guid.NewGuid();
        var defaultPolicyId = Guid.NewGuid();
        var modelPolicyId = Guid.NewGuid();
        var limiter = new RequestRateLimiter();
        limiter.ReplacePolicies([
            new RateLimitPolicySnapshot(defaultPolicyId, credentialId, null, 10, 60, true),
            new RateLimitPolicySnapshot(modelPolicyId, credentialId, "agic-code", 1, 60, true)
        ]);
        var now = DateTimeOffset.UtcNow;

        Assert.True(limiter.TryAcquire(credentialId, "agic-code", now).Allowed);
        var rejected = limiter.TryAcquire(credentialId, "AGIC-CODE", now.AddSeconds(1));
        Assert.False(rejected.Allowed);
        Assert.Equal(modelPolicyId, rejected.Policy?.Id);

        Assert.True(limiter.TryAcquire(credentialId, "agic-code-fast", now.AddSeconds(1)).Allowed);
        Assert.True(limiter.TryAcquire(credentialId, "agic-code-fast", now.AddSeconds(2)).Allowed);
    }

    [Fact]
    public void No_policy_allows_request()
    {
        var limiter = new RequestRateLimiter();

        var result = limiter.TryAcquire(Guid.NewGuid(), "agic-code", DateTimeOffset.UtcNow);

        Assert.True(result.Allowed);
        Assert.Null(result.Policy);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_exceed_policy_limit()
    {
        var credentialId = Guid.NewGuid();
        var limiter = new RequestRateLimiter();
        limiter.ReplacePolicies([
            new RateLimitPolicySnapshot(Guid.NewGuid(), credentialId, null, 8, 60, true)
        ]);
        var now = DateTimeOffset.UtcNow;

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            limiter.TryAcquire(credentialId, "agic-code", now).Allowed)));

        Assert.Equal(8, results.Count(allowed => allowed));
        Assert.Equal(24, results.Count(allowed => !allowed));
    }

    [Fact]
    public async Task User_policy_aggregates_requests_across_personal_credentials()
    {
        var userPolicyId = Guid.NewGuid();
        var credentialA = Guid.NewGuid();
        var credentialB = Guid.NewGuid();
        var limiter = new RequestRateLimiter();
        limiter.ReplacePolicies([
            new RateLimitPolicySnapshot(
                userPolicyId,
                Guid.Empty,
                null,
                2,
                60,
                true,
                OwnerTenantId: "tenant-1",
                OwnerObjectId: "user-1")
        ]);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await limiter.TryAcquireAsync(credentialA, "agic-code", "TENANT-1", "USER-1", now, TestContext.Current.CancellationToken)).Allowed);
        Assert.True((await limiter.TryAcquireAsync(credentialB, "agic-code", "tenant-1", "user-1", now.AddSeconds(1), TestContext.Current.CancellationToken)).Allowed);

        var rejected = await limiter.TryAcquireAsync(
            credentialA,
            "agic-code",
            "tenant-1",
            "user-1",
            now.AddSeconds(2), TestContext.Current.CancellationToken);

        Assert.False(rejected.Allowed);
        Assert.Equal(userPolicyId, rejected.Policy?.Id);
    }

    [Fact]
    public async Task User_and_credential_policies_are_acquired_atomically()
    {
        var userPolicyId = Guid.NewGuid();
        var credentialPolicyId = Guid.NewGuid();
        var credentialA = Guid.NewGuid();
        var credentialB = Guid.NewGuid();
        var limiter = new RequestRateLimiter();
        limiter.ReplacePolicies([
            new RateLimitPolicySnapshot(
                userPolicyId,
                Guid.Empty,
                null,
                2,
                60,
                true,
                OwnerTenantId: "tenant-1",
                OwnerObjectId: "user-1"),
            new RateLimitPolicySnapshot(credentialPolicyId, credentialA, null, 1, 60, true)
        ]);
        var now = DateTimeOffset.UtcNow;

        Assert.True((await limiter.TryAcquireAsync(
            credentialA,
            "agic-code",
            "tenant-1",
            "user-1",
            now, TestContext.Current.CancellationToken)).Allowed);

        var credentialRejected = await limiter.TryAcquireAsync(
            credentialA,
            "agic-code",
            "tenant-1",
            "user-1",
            now.AddSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(credentialRejected.Allowed);
        Assert.Equal(credentialPolicyId, credentialRejected.Policy?.Id);

        Assert.True((await limiter.TryAcquireAsync(
            credentialB,
            "agic-code",
            "tenant-1",
            "user-1",
            now.AddSeconds(2), TestContext.Current.CancellationToken)).Allowed);

        var userRejected = await limiter.TryAcquireAsync(
            credentialB,
            "agic-code",
            "tenant-1",
            "user-1",
            now.AddSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(userRejected.Allowed);
        Assert.Equal(userPolicyId, userRejected.Policy?.Id);
    }

}
