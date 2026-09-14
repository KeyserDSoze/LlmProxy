using System.Collections.Concurrent;

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

        var activeIds = next.Values.Select(policy => policy.Id).ToHashSet();
        foreach (var policyId in _counters.Keys)
        {
            if (!activeIds.Contains(policyId))
            {
                _counters.TryRemove(policyId, out _);
            }
        }
    }

    public RateLimitDecision TryAcquire(Guid apiCredentialId, string logicalModel, DateTimeOffset nowUtc)
    {
        if (apiCredentialId == Guid.Empty)
        {
            return RateLimitDecision.Permit();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(logicalModel);

        var policies = Volatile.Read(ref _policies);
        var normalizedModel = NormalizeModel(logicalModel);

        if (!policies.TryGetValue(new RateLimitKey(apiCredentialId, normalizedModel), out var policy) &&
            !policies.TryGetValue(new RateLimitKey(apiCredentialId, string.Empty), out policy))
        {
            return RateLimitDecision.Permit();
        }

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
                return RateLimitDecision.Reject(policy, retryAfterSeconds);
            }

            counter.Count++;
            return RateLimitDecision.Permit(policy);
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
