using LlmProxy.Application.Governance;

namespace LlmProxy.UnitTests.Application;

public sealed class OutputTokenBudgetTests
{
    [Fact]
    public async Task Reservation_blocks_oversubscription_and_settlement_refunds_unused_tokens()
    {
        var store = new InMemoryOutputTokenBudgetStore();
        var policyId = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await store.TryReserveAsync(policyId, 100, 60, 60, now, cancellationToken);
        Assert.True(first.Acquired);
        Assert.NotNull(first.Reservation);

        var rejected = await store.TryReserveAsync(policyId, 100, 60, 50, now.AddSeconds(1), cancellationToken);
        Assert.False(rejected.Acquired);
        Assert.Equal(OutputTokenBudgetAdmissionFailure.BudgetExceeded, rejected.Failure);

        await first.Reservation!.SettleAsync(20, usageCertain: true, cancellationToken);

        var afterRefund = await store.TryReserveAsync(policyId, 100, 60, 50, now.AddSeconds(2), cancellationToken);
        Assert.True(afterRefund.Acquired);
        Assert.Equal(70, afterRefund.WindowUsage);
    }

    [Fact]
    public async Task Uncertain_usage_keeps_full_reservation_charged()
    {
        var store = new InMemoryOutputTokenBudgetStore();
        var policyId = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await store.TryReserveAsync(policyId, 100, 60, 80, now, cancellationToken);
        await first.Reservation!.SettleAsync(null, usageCertain: false, cancellationToken);

        var rejected = await store.TryReserveAsync(policyId, 100, 60, 21, now.AddSeconds(1), cancellationToken);
        Assert.False(rejected.Acquired);
        Assert.Equal(80, rejected.WindowUsage);
    }

    [Fact]
    public async Task New_window_resets_budget()
    {
        var store = new InMemoryOutputTokenBudgetStore();
        var policyId = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True((await store.TryReserveAsync(policyId, 50, 60, 50, now, cancellationToken)).Acquired);
        Assert.False((await store.TryReserveAsync(policyId, 50, 60, 1, now.AddSeconds(1), cancellationToken)).Acquired);
        Assert.True((await store.TryReserveAsync(policyId, 50, 60, 50, now.AddSeconds(61), cancellationToken)).Acquired);
    }

    [Fact]
    public async Task Concurrent_reservations_cannot_exceed_budget()
    {
        var store = new InMemoryOutputTokenBudgetStore();
        var policyId = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var cancellationToken = TestContext.Current.CancellationToken;

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            store.TryReserveAsync(policyId, 100, 60, 10, now, cancellationToken).AsTask()));

        Assert.Equal(10, decisions.Count(decision => decision.Acquired));
        Assert.Equal(10, decisions.Count(decision => !decision.Acquired));
    }
}
