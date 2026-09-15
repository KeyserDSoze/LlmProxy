using System.Collections.Concurrent;
using LlmProxy.Application.Observability;

namespace LlmProxy.Application.Governance;

public sealed record RateLimitPolicySnapshot(
    Guid Id,
    Guid ApiCredentialId,
    string? LogicalModel,
    int RequestsPerWindow,
    int WindowSeconds,
    bool Enabled);

public sealed record RateLimitDecision(
    bool Allowed,
    int RetryAfterSeconds,
    RateLimitPolicySnapshot? Policy)
{
    public static RateLimitDecision Permit(RateLimitPolicySnapshot? policy = null) => new(true, 0, policy);
    public static RateLimitDecision Reject(RateLimitPolicySnapshot policy, int retryAfterSeconds) =>
        new(false, Math.Max(1, retryAfterSeconds), policy);
}

public sealed class RequestRateLimiter
{
    private IReadOnlyDictionary<RateLimitKey, RateLimitPolicySnapshot> _policies =
        new Dictionary<RateLimitKey, RateLimitPolicySnapshot>();

    private readonly ConcurrentDictionary<Guid, WindowCounter> _counters = new();

    public void ReplacePolicies(IEnumerable<RateLimitPolicySnapshot> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var next = policies
            .Where(policy => policy.Enabled)
            .ToDictionary(
                policy => new RateLimitKey(policy.ApiCredentialId, NormalizeModel(policy.LogicalModel)),
                policy => policy);

        Volatile.Write(ref _policies, next);
        RemoveInactiveCounters(next.Values.Select(policy => policy.Id));
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
        else
        {
            _counters.TryRemove(policy.Id, out _);
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
        _counters.TryRemove(policyId, out _);
    }

    public RateLimitDecision TryAcquire(Guid apiCredentialId, string logicalModel, DateTimeOffset nowUtc)
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

        var policies = Volatile.Read(ref _policies);
        var normalizedModel = NormalizeModel(logicalModel);

        if (!policies.TryGetValue(new RateLimitKey(apiCredentialId, normalizedModel), out var policy) &&
            !policies.TryGetValue(new RateLimitKey(apiCredentialId, string.Empty), out policy))
        {
            activity?.SetTag("llmproxy.rate_limit.result", "no_policy");
            return RateLimitDecision.Permit();
        }

        LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.policy_id", policy.Id);
        activity?.SetTag("llmproxy.rate_limit.requests_per_window", policy.RequestsPerWindow);
        activity?.SetTag("llmproxy.rate_limit.window_seconds", policy.WindowSeconds);

        var counter = _counters.GetOrAdd(policy.Id, _ => new WindowCounter(nowUtc));
        lock (counter.SyncRoot)
        {
            var window = TimeSpan.FromSeconds(policy.WindowSeconds);
            if (nowUtc < counter.WindowStartUtc || nowUtc - counter.WindowStartUtc >= window)
            {
                counter.WindowStartUtc = nowUtc;
                counter.Count = 0;
            }

            if (counter.Count >= policy.RequestsPerWindow)
            {
                var remaining = counter.WindowStartUtc.Add(window) - nowUtc;
                var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                activity?.SetTag("llmproxy.rate_limit.result", "rejected");
                activity?.SetTag("llmproxy.rate_limit.retry_after_seconds", retryAfterSeconds);
                LlmProxyActivity.MarkError(activity, "rate_limit_exceeded");
                return RateLimitDecision.Reject(policy, retryAfterSeconds);
            }

            counter.Count++;
            activity?.SetTag("llmproxy.rate_limit.result", "allowed");
            activity?.SetTag("llmproxy.rate_limit.window_count", counter.Count);
            return RateLimitDecision.Permit(policy);
        }
    }

    private void RemoveInactiveCounters(IEnumerable<Guid> activePolicyIds)
    {
        var activeIds = activePolicyIds.ToHashSet();
        foreach (var policyId in _counters.Keys)
        {
            if (!activeIds.Contains(policyId))
            {
                _counters.TryRemove(policyId, out _);
            }
        }
    }

    private static string NormalizeModel(string? logicalModel) =>
        string.IsNullOrWhiteSpace(logicalModel)
            ? string.Empty
            : logicalModel.Trim().ToUpperInvariant();

    private readonly record struct RateLimitKey(Guid ApiCredentialId, string LogicalModel);

    private sealed class WindowCounter(DateTimeOffset windowStartUtc)
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset WindowStartUtc { get; set; } = windowStartUtc;
        public int Count { get; set; }
    }
}
