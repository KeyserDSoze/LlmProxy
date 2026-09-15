using System.Collections.Concurrent;
using LlmProxy.Application.Observability;

namespace LlmProxy.Application.Governance;

public sealed record RateLimitPolicySnapshot(
    Guid Id,
    Guid ApiCredentialId,
    string? LogicalModel,
    int RequestsPerWindow,
    int WindowSeconds,
    bool Enabled,
    int? OutputTokensPerWindow = null,
    int? MaxOutputTokensPerRequest = null)
{
    public bool HasOutputTokenBudget => OutputTokensPerWindow.HasValue && MaxOutputTokensPerRequest.HasValue;
}

public sealed record RateLimitDecision(
    bool Allowed,
    int RetryAfterSeconds,
    RateLimitPolicySnapshot? Policy)
{
    public static RateLimitDecision Permit(RateLimitPolicySnapshot? policy = null) => new(true, 0, policy);
    public static RateLimitDecision Reject(RateLimitPolicySnapshot policy, int retryAfterSeconds) =>
        new(false, Math.Max(1, retryAfterSeconds), policy);
}

public sealed record RateLimitCounterDecision(
    bool Allowed,
    int RetryAfterSeconds,
    int WindowCount,
    string Provider,
    bool Degraded = false);

public interface IRateLimitCounterStore
{
    ValueTask<RateLimitCounterDecision> TryAcquireAsync(
        Guid policyId,
        int requestsPerWindow,
        int windowSeconds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryRateLimitCounterStore : IRateLimitCounterStore
{
    private readonly ConcurrentDictionary<CounterKey, WindowCounter> _counters = new();

    public ValueTask<RateLimitCounterDecision> TryAcquireAsync(
        Guid policyId,
        int requestsPerWindow,
        int windowSeconds,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSeconds, 1);

        var key = new CounterKey(policyId, requestsPerWindow, windowSeconds);
        var counter = _counters.GetOrAdd(key, _ => new WindowCounter(nowUtc));
        lock (counter.SyncRoot)
        {
            var window = TimeSpan.FromSeconds(windowSeconds);
            if (nowUtc < counter.WindowStartUtc || nowUtc - counter.WindowStartUtc >= window)
            {
                counter.WindowStartUtc = nowUtc;
                counter.Count = 0;
            }

            if (counter.Count >= requestsPerWindow)
            {
                var remaining = counter.WindowStartUtc.Add(window) - nowUtc;
                var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                return ValueTask.FromResult(new RateLimitCounterDecision(
                    false,
                    retryAfterSeconds,
                    counter.Count,
                    "local"));
            }

            counter.Count++;
            return ValueTask.FromResult(new RateLimitCounterDecision(
                true,
                0,
                counter.Count,
                "local"));
        }
    }

    private readonly record struct CounterKey(Guid PolicyId, int RequestsPerWindow, int WindowSeconds);

    private sealed class WindowCounter(DateTimeOffset windowStartUtc)
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset WindowStartUtc { get; set; } = windowStartUtc;
        public int Count { get; set; }
    }
}

public sealed class RequestRateLimiter(IRateLimitCounterStore counterStore)
{
    public RequestRateLimiter() : this(new InMemoryRateLimitCounterStore())
    {
    }

    private IReadOnlyDictionary<RateLimitKey, RateLimitPolicySnapshot> _policies =
        new Dictionary<RateLimitKey, RateLimitPolicySnapshot>();

    public void ReplacePolicies(IEnumerable<RateLimitPolicySnapshot> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var next = policies
            .Where(policy => policy.Enabled)
            .ToDictionary(
                policy => new RateLimitKey(policy.ApiCredentialId, NormalizeModel(policy.LogicalModel)),
                policy => policy);

        Volatile.Write(ref _policies, next);
    }

    public void UpsertPolicy(RateLimitPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var current = Volatile.Read(ref _policies);
        var next = current
            .Where(item => item.Value.Id != policy.Id)
            .ToDictionary(item => item.Key, item => item.Value);

        if (policy.Enabled)
        {
            next[new RateLimitKey(policy.ApiCredentialId, NormalizeModel(policy.LogicalModel))] = policy;
        }

        Volatile.Write(ref _policies, next);
    }

    public void RemovePolicy(Guid policyId)
    {
        if (policyId == Guid.Empty)
        {
            return;
        }

        var current = Volatile.Read(ref _policies);
        var next = current
            .Where(item => item.Value.Id != policyId)
            .ToDictionary(item => item.Key, item => item.Value);
        Volatile.Write(ref _policies, next);
    }

    public RateLimitPolicySnapshot? ResolvePolicy(Guid apiCredentialId, string logicalModel)
    {
        if (apiCredentialId == Guid.Empty || string.IsNullOrWhiteSpace(logicalModel))
        {
            return null;
        }

        var policies = Volatile.Read(ref _policies);
        var normalizedModel = NormalizeModel(logicalModel);
        return policies.TryGetValue(new RateLimitKey(apiCredentialId, normalizedModel), out var exact)
            ? exact
            : policies.TryGetValue(new RateLimitKey(apiCredentialId, string.Empty), out var fallback)
                ? fallback
                : null;
    }

    public RateLimitDecision TryAcquire(Guid apiCredentialId, string logicalModel, DateTimeOffset nowUtc)
        => TryAcquireAsync(apiCredentialId, logicalModel, nowUtc).AsTask().GetAwaiter().GetResult();

    public async ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid apiCredentialId,
        string logicalModel,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        using var activity = LlmProxyActivity.Start("llmproxy.governance.rate_limit");
        LlmProxyActivity.SetGuid(activity, "llmproxy.api_credential.id", apiCredentialId);
        activity?.SetTag("llmproxy.logical_model", logicalModel);

        if (apiCredentialId == Guid.Empty)
        {
            activity?.SetTag("llmproxy.rate_limit.result", "not_applicable");
            return RateLimitDecision.Permit();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(logicalModel);
        var policy = ResolvePolicy(apiCredentialId, logicalModel);
        if (policy is null)
        {
            activity?.SetTag("llmproxy.rate_limit.result", "no_policy");
            return RateLimitDecision.Permit();
        }

        LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.policy_id", policy.Id);
        activity?.SetTag("llmproxy.rate_limit.requests_per_window", policy.RequestsPerWindow);
        activity?.SetTag("llmproxy.rate_limit.window_seconds", policy.WindowSeconds);

        var counterDecision = await counterStore.TryAcquireAsync(
            policy.Id,
            policy.RequestsPerWindow,
            policy.WindowSeconds,
            nowUtc,
            cancellationToken);

        activity?.SetTag("llmproxy.rate_limit.provider", counterDecision.Provider);
        activity?.SetTag("llmproxy.rate_limit.degraded", counterDecision.Degraded);
        activity?.SetTag("llmproxy.rate_limit.window_count", counterDecision.WindowCount);

        if (!counterDecision.Allowed)
        {
            activity?.SetTag("llmproxy.rate_limit.result", "rejected");
            activity?.SetTag("llmproxy.rate_limit.retry_after_seconds", counterDecision.RetryAfterSeconds);
            LlmProxyActivity.MarkError(activity, "rate_limit_exceeded");
            return RateLimitDecision.Reject(policy, counterDecision.RetryAfterSeconds);
        }

        activity?.SetTag("llmproxy.rate_limit.result", "allowed");
        return RateLimitDecision.Permit(policy);
    }

    private static string NormalizeModel(string? logicalModel) =>
        string.IsNullOrWhiteSpace(logicalModel)
            ? string.Empty
            : logicalModel.Trim().ToUpperInvariant();

    private readonly record struct RateLimitKey(Guid ApiCredentialId, string LogicalModel);
}
