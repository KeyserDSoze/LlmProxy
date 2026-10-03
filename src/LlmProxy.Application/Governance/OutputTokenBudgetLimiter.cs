using System.Collections.Concurrent;
using LlmProxy.Application.Observability;

namespace LlmProxy.Application.Governance;

public enum OutputTokenBudgetAdmissionFailure
{
    None = 0,
    BudgetExceeded = 1,
    CoordinationUnavailable = 2
}

public sealed record OutputTokenBudgetStoreDecision(
    bool Acquired,
    OutputTokenBudgetAdmissionFailure Failure,
    int RetryAfterSeconds,
    long WindowUsage,
    string Provider,
    IOutputTokenBudgetReservation? Reservation)
{
    public static OutputTokenBudgetStoreDecision Permit(
        long windowUsage,
        string provider,
        IOutputTokenBudgetReservation reservation) =>
        new(true, OutputTokenBudgetAdmissionFailure.None, 0, windowUsage, provider, reservation);

    public static OutputTokenBudgetStoreDecision Reject(
        int retryAfterSeconds,
        long windowUsage,
        string provider) =>
        new(false, OutputTokenBudgetAdmissionFailure.BudgetExceeded, Math.Max(1, retryAfterSeconds), windowUsage, provider, null);

    public static OutputTokenBudgetStoreDecision Unavailable(string provider) =>
        new(false, OutputTokenBudgetAdmissionFailure.CoordinationUnavailable, 1, 0, provider, null);
}

public interface IOutputTokenBudgetReservation
{
    int ReservedTokens { get; }

    ValueTask SettleAsync(
        int? actualOutputTokens,
        bool usageCertain,
        CancellationToken cancellationToken = default);
}

public interface IOutputTokenBudgetStore
{
    ValueTask<OutputTokenBudgetStoreDecision> TryReserveAsync(
        Guid policyId,
        int outputTokensPerWindow,
        int windowSeconds,
        int reservationTokens,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryOutputTokenBudgetStore : IOutputTokenBudgetStore
{
    private readonly ConcurrentDictionary<BudgetKey, WindowState> _states = new();

    public ValueTask<OutputTokenBudgetStoreDecision> TryReserveAsync(
        Guid policyId,
        int outputTokensPerWindow,
        int windowSeconds,
        int reservationTokens,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(outputTokensPerWindow, windowSeconds, reservationTokens);

        var key = new BudgetKey(policyId, outputTokensPerWindow, windowSeconds);
        var state = _states.GetOrAdd(key, _ => new WindowState());
        var windowStartUtc = WindowStart(nowUtc, windowSeconds);

        lock (state.SyncRoot)
        {
            if (state.WindowStartUtc != windowStartUtc)
            {
                state.WindowStartUtc = windowStartUtc;
                state.UsedTokens = 0;
            }

            if (state.UsedTokens + reservationTokens > outputTokensPerWindow)
            {
                var retryAfter = Math.Max(
                    1,
                    (int)Math.Ceiling((windowStartUtc.AddSeconds(windowSeconds) - nowUtc).TotalSeconds));
                return ValueTask.FromResult(OutputTokenBudgetStoreDecision.Reject(
                    retryAfter,
                    state.UsedTokens,
                    "local"));
            }

            state.UsedTokens += reservationTokens;
            var reservation = new LocalReservation(state, windowStartUtc, reservationTokens);
            return ValueTask.FromResult(OutputTokenBudgetStoreDecision.Permit(
                state.UsedTokens,
                "local",
                reservation));
        }
    }

    private static void Validate(int outputTokensPerWindow, int windowSeconds, int reservationTokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputTokensPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(reservationTokens, 1);
        if (reservationTokens > outputTokensPerWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(reservationTokens), "Reservation cannot exceed the window budget.");
        }
    }

    private static DateTimeOffset WindowStart(DateTimeOffset nowUtc, int windowSeconds)
    {
        var seconds = nowUtc.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(seconds - seconds % windowSeconds);
    }

    private readonly record struct BudgetKey(Guid PolicyId, int OutputTokensPerWindow, int WindowSeconds);

    private sealed class WindowState
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset WindowStartUtc { get; set; } = DateTimeOffset.MinValue;
        public long UsedTokens { get; set; }
    }

    private sealed class LocalReservation(
        WindowState state,
        DateTimeOffset windowStartUtc,
        int reservedTokens) : IOutputTokenBudgetReservation
    {
        private int _settled;

        public int ReservedTokens { get; } = reservedTokens;

        public ValueTask SettleAsync(
            int? actualOutputTokens,
            bool usageCertain,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _settled, 1) != 0 ||
                !usageCertain ||
                actualOutputTokens is not int actual ||
                actual < 0 ||
                actual > ReservedTokens)
            {
                return ValueTask.CompletedTask;
            }

            var refund = ReservedTokens - actual;
            if (refund == 0)
            {
                return ValueTask.CompletedTask;
            }

            lock (state.SyncRoot)
            {
                if (state.WindowStartUtc == windowStartUtc)
                {
                    state.UsedTokens = Math.Max(0, state.UsedTokens - refund);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}

public sealed class OutputTokenBudgetLimiter(IOutputTokenBudgetStore store)
{
    public ValueTask<OutputTokenBudgetStoreDecision> TryReserveAsync(
        RateLimitPolicySnapshot? policy,
        int reservationTokens,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (policy is null)
        {
            throw new InvalidOperationException("Output-token reservation requires a policy.");
        }

        return TryReserveAsync([policy], reservationTokens, nowUtc, cancellationToken);
    }

    public async ValueTask<OutputTokenBudgetStoreDecision> TryReserveAsync(
        IReadOnlyList<RateLimitPolicySnapshot> policies,
        int reservationTokens,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (policies.Count == 0)
        {
            throw new InvalidOperationException("Output-token reservation requires at least one policy.");
        }

        var reservations = new List<IOutputTokenBudgetReservation>(policies.Count);
        OutputTokenBudgetStoreDecision? lastDecision = null;

        foreach (var policy in policies.OrderBy(item => item.Id))
        {
            if (policy.OutputTokensPerWindow is not int budget ||
                policy.MaxOutputTokensPerRequest is not int maxPerRequest)
            {
                throw new InvalidOperationException("Output-token reservation requires complete budget configuration on every applicable policy.");
            }

            if (reservationTokens is < 1 || reservationTokens > maxPerRequest)
            {
                throw new ArgumentOutOfRangeException(nameof(reservationTokens));
            }

            using var activity = LlmProxyActivity.Start("llmproxy.governance.output_token_budget");
            LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.policy_id", policy.Id);
            activity?.SetTag("llmproxy.output_token_budget.scope",
                policy.IsUserScoped ? "user" : policy.IsUsageGroupScoped ? "usage_group" : "credential");
            activity?.SetTag("llmproxy.output_token_budget.tokens_per_window", budget);
            activity?.SetTag("llmproxy.output_token_budget.max_per_request", maxPerRequest);
            activity?.SetTag("llmproxy.output_token_budget.reservation", reservationTokens);
            activity?.SetTag("llmproxy.output_token_budget.window_seconds", policy.WindowSeconds);

            var decision = await store.TryReserveAsync(
                policy.Id,
                budget,
                policy.WindowSeconds,
                reservationTokens,
                nowUtc,
                cancellationToken);
            lastDecision = decision;

            activity?.SetTag("llmproxy.output_token_budget.provider", decision.Provider);
            activity?.SetTag("llmproxy.output_token_budget.window_usage", decision.WindowUsage);
            activity?.SetTag("llmproxy.output_token_budget.result", decision.Failure switch
            {
                OutputTokenBudgetAdmissionFailure.None => "allowed",
                OutputTokenBudgetAdmissionFailure.BudgetExceeded => "rejected",
                OutputTokenBudgetAdmissionFailure.CoordinationUnavailable => "coordination_unavailable",
                _ => "unknown"
            });

            if (!decision.Acquired || decision.Reservation is null)
            {
                LlmProxyActivity.MarkError(activity, decision.Failure == OutputTokenBudgetAdmissionFailure.BudgetExceeded
                    ? "token_budget_exceeded"
                    : "token_budget_coordination_unavailable");

                foreach (var acquired in reservations)
                {
                    await acquired.SettleAsync(0, usageCertain: true, CancellationToken.None);
                }

                return decision;
            }

            reservations.Add(decision.Reservation);
        }

        return OutputTokenBudgetStoreDecision.Permit(
            lastDecision?.WindowUsage ?? 0,
            lastDecision?.Provider ?? "local",
            new CompositeReservation(reservations, reservationTokens));
    }

    private sealed class CompositeReservation(
        IReadOnlyList<IOutputTokenBudgetReservation> reservations,
        int reservedTokens) : IOutputTokenBudgetReservation
    {
        private int _settled;

        public int ReservedTokens { get; } = reservedTokens;

        public async ValueTask SettleAsync(
            int? actualOutputTokens,
            bool usageCertain,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                return;
            }

            foreach (var reservation in reservations)
            {
                await reservation.SettleAsync(actualOutputTokens, usageCertain, cancellationToken);
            }
        }
    }
}
