using System.Text.Json;
using LlmProxy.Application.Governance;
using LlmProxy.Infrastructure.Routing;
using LlmProxy.Infrastructure.Runtime;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class RuntimeStateOutboxPublisherTests
{
    [Fact]
    public async Task PublishAsync_AppliesAcknowledgedRatePolicyToLocalL1()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new RecordingTransportPublisher();
        var rateLimiter = new RequestRateLimiter();
        var publisher = new RuntimeStateOutboxPublisher(
            transport,
            new InMemoryRouteCatalog(),
            new InMemoryApiCredentialCache(),
            rateLimiter);

        var credentialId = Guid.NewGuid();
        var policy = new RateLimitPolicySnapshot(
            Guid.NewGuid(),
            credentialId,
            "agic-code-fast",
            1,
            60,
            true);

        await publisher.PublishAsync(new RuntimeStateChange(
            "rate-policy",
            "upsert",
            policy.Id,
            JsonSerializer.Serialize(policy),
            DateTimeOffset.UtcNow), cancellationToken);

        Assert.Single(transport.Published);

        var first = await rateLimiter.TryAcquireAsync(
            credentialId,
            "agic-code-fast",
            DateTimeOffset.UtcNow,
            cancellationToken);
        var second = await rateLimiter.TryAcquireAsync(
            credentialId,
            "agic-code-fast",
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal(policy.Id, second.Policy?.Id);
    }

    [Fact]
    public async Task PublishAsync_DoesNotMutateLocalL1WhenTransportFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new RecordingTransportPublisher
        {
            Exception = new InvalidOperationException("redis unavailable")
        };
        var rateLimiter = new RequestRateLimiter();
        var publisher = new RuntimeStateOutboxPublisher(
            transport,
            new InMemoryRouteCatalog(),
            new InMemoryApiCredentialCache(),
            rateLimiter);

        var credentialId = Guid.NewGuid();
        var policy = new RateLimitPolicySnapshot(
            Guid.NewGuid(),
            credentialId,
            "agic-code-fast",
            1,
            60,
            true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(new RuntimeStateChange(
            "rate-policy",
            "upsert",
            policy.Id,
            JsonSerializer.Serialize(policy),
            DateTimeOffset.UtcNow), cancellationToken));

        var first = await rateLimiter.TryAcquireAsync(
            credentialId,
            "agic-code-fast",
            DateTimeOffset.UtcNow,
            cancellationToken);
        var second = await rateLimiter.TryAcquireAsync(
            credentialId,
            "agic-code-fast",
            DateTimeOffset.UtcNow,
            cancellationToken);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Null(first.Policy);
        Assert.Null(second.Policy);
    }

    private sealed class RecordingTransportPublisher : IRuntimeStateTransportPublisher
    {
        public List<RuntimeStateChange> Published { get; } = [];
        public Exception? Exception { get; init; }

        public Task PublishAsync(RuntimeStateChange change, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Published.Add(change);
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }
}
