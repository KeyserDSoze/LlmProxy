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
    int? MaxOutputTokensPerRequest = null,
    string? OwnerTenantId = null,
    string? OwnerObjectId = null)
{
    public bool HasOutputTokenBudget => OutputTokensPerWindow.HasValue && MaxOutputTokensPerRequest.HasValue;
    public bool IsUserScoped =>
        ApiCredentialId == Guid.Empty &&
        !string.IsNullOrWhiteSpace(OwnerTenantId) &&
        !string.IsNullOrWhiteSpace(OwnerObjectId);
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

public sealed record RateLimitCounterRequest(
    Guid PolicyId,
    int RequestsPerWindow,
    int WindowSeconds);

public sealed record RateLimitCounterDecision(
    bool Allowed,
    int RetryAfterSeconds,
    int WindowCount,
    string Provider,
    bool Degraded = false,
    Guid? RejectedPolicyId = null);

public interface IRateLimitCounterStore
{
    ValueTask<RateLimitCounterDecision> TryAcquireAsync(
        IReadOnlyList<RateLimitCounterRequest> requests,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryRateLimitCounterStore : IRateLimitCounterStore
{
    private readonly Dictionary<CounterKey, WindowCounter> _counters = new();
    private readonly object _gate = new();

    public ValueTask<RateLimitCounterDecision> TryAcquireAsync(
        IReadOnlyList<RateLimitCounterRequest> requests,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requests.Count == 0)
        {
            return ValueTask.FromResult(new RateLimitCounterDecision(true, 0, 0, "local"));
        }

        foreach (var request in requests)
        {
            Validate(request);
        }

        lock (_gate)
        {
            var resolved = new List<(RateLimitCounterRequest Request, WindowCounter Counter, CounterKey Key)>(requests.Count);
            foreach (var request in requests)
            {
                var key = new CounterKey(request.PolicyId, request.RequestsPerWindow, request.WindowSeconds);
                if (!_counters.TryGetValue(key, out var counter))
                {
                    counter = new WindowCounter(nowUtc);
                    _counters.Add(key, counter);
                }

                var window = TimeSpan.FromSeconds(request.WindowSeconds);
                if (nowUtc < counter.WindowStartUtc || nowUtc - counter.WindowStartUtc >= window)
                {
                    counter.WindowStartUtc = nowUtc;
                    counter.Count = 0;
                }

                resolved.Add((request, counter, key));
            }

            (RateLimitCounterRequest Request, WindowCounter Counter)? rejected = null;
            var retryAfterSeconds = 0;
            foreach (var item in resolved)
            {
                if (item.Counter.Count < item.Request.RequestsPerWindow)
                {
                    continue;
                }

                var remaining = item.Counter.WindowStartUtc.AddSeconds(item.Request.WindowSeconds) - nowUtc;
                var retry = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                if (rejected is null || retry > retryAfterSeconds)
                {
                    rejected = (item.Request, item.Counter);
                    retryAfterSeconds = retry;
                }
            }

            if (rejected is not null)
            {
                return ValueTask.FromResult(new RateLimitCounterDecision(
                    false,
                    retryAfterSeconds,
                    rejected.Value.Counter.Count,
                    "local",
                    RejectedPolicyId: rejected.Value.Request.PolicyId));
            }

            var maxWindowCount = 0;
            foreach (var item in resolved)
            {
                item.Counter.Count++;
                maxWindowCount = Math.Max(maxWindowCount, item.Counter.Count);
            }

            return ValueTask.FromResult(new RateLimitCounterDecision(
                true,
                0,
                maxWindowCount,
                "local"));
        }
    }

    private static void Validate(RateLimitCounterRequest request)
    {
        if (request.PolicyId == Guid.Empty)
        {
            throw new ArgumentException("Policy id is required.", nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.RequestsPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.WindowSeconds, 1);
    }

    private readonly record struct CounterKey(Guid PolicyId, int RequestsPerWindow, int WindowSeconds);

    private sealed class WindowCounter(DateTimeOffset windowStartUtc)
    {
        public DateTimeOffset WindowStartUtc { get; set; } = windowStartUtc;
        public int Count { get; set; }
    }
}

public sealed class RequestRateLimiter(IRateLimitCounterStore counterStore)
{
    public RequestRateLimiter() : this(new InMemoryRateLimitCounterStore())
    {
    }

    private IReadOnlyDictionary<CredentialRateLimitKey, RateLimitPolicySnapshot> _credentialPolicies =
        new Dictionary<CredentialRateLimitKey, RateLimitPolicySnapshot>();

    private IReadOnlyDictionary<UserRateLimitKey, RateLimitPolicySnapshot> _userPolicies =
        new Dictionary<UserRateLimitKey, RateLimitPolicySnapshot>();

    public void ReplacePolicies(IEnumerable<RateLimitPolicySnapshot> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var credentials = new Dictionary<CredentialRateLimitKey, RateLimitPolicySnapshot>();
        var users = new Dictionary<UserRateLimitKey, RateLimitPolicySnapshot>();

        foreach (var policy in policies.Where(policy => policy.Enabled))
        {
            AddPolicy(credentials, users, policy);
        }

        Volatile.Write(ref _credentialPolicies, credentials);
        Volatile.Write(ref _userPolicies, users);
    }

    public void UpsertPolicy(RateLimitPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var credentialNext = Volatile.Read(ref _credentialPolicies)
            .Where(item => item.Value.Id != policy.Id)
            .ToDictionary(item => item.Key, item => item.Value);
        var userNext = Volatile.Read(ref _userPolicies)
            .Where(item => item.Value.Id != policy.Id)
            .ToDictionary(item => item.Key, item => item.Value);

        if (policy.Enabled)
        {
            AddPolicy(credentialNext, userNext, policy);
        }

        Volatile.Write(ref _credentialPolicies, credentialNext);
        Volatile.Write(ref _userPolicies, userNext);
    }

    public void RemovePolicy(Guid policyId)
    {
        if (policyId == Guid.Empty)
        {
            return;
        }

        var credentialNext = Volatile.Read(ref _credentialPolicies)
            .Where(item => item.Value.Id != policyId)
            .ToDictionary(item => item.Key, item => item.Value);
        var userNext = Volatile.Read(ref _userPolicies)
            .Where(item => item.Value.Id != policyId)
            .ToDictionary(item => item.Key, item => item.Value);
        Volatile.Write(ref _credentialPolicies, credentialNext);
        Volatile.Write(ref _userPolicies, userNext);
    }

    public RateLimitPolicySnapshot? ResolvePolicy(Guid apiCredentialId, string logicalModel)
    {
        if (apiCredentialId == Guid.Empty || string.IsNullOrWhiteSpace(logicalModel))
        {
            return null;
        }

        var policies = Volatile.Read(ref _credentialPolicies);
        var normalizedModel = NormalizeModel(logicalModel);
        return policies.TryGetValue(new CredentialRateLimitKey(apiCredentialId, normalizedModel), out var exact)
            ? exact
            : policies.TryGetValue(new CredentialRateLimitKey(apiCredentialId, string.Empty), out var fallback)
                ? fallback
                : null;
    }

    public RateLimitPolicySnapshot? ResolveUserPolicy(
        string? ownerTenantId,
        string? ownerObjectId,
        string logicalModel)
    {
        if (string.IsNullOrWhiteSpace(ownerTenantId) ||
            string.IsNullOrWhiteSpace(ownerObjectId) ||
            string.IsNullOrWhiteSpace(logicalModel))
        {
            return null;
        }

        var policies = Volatile.Read(ref _userPolicies);
        var normalizedModel = NormalizeModel(logicalModel);
        var tenant = NormalizeIdentity(ownerTenantId);
        var subject = NormalizeIdentity(ownerObjectId);
        return policies.TryGetValue(new UserRateLimitKey(tenant, subject, normalizedModel), out var exact)
            ? exact
            : policies.TryGetValue(new UserRateLimitKey(tenant, subject, string.Empty), out var fallback)
                ? fallback
                : null;
    }

    public RateLimitDecision TryAcquire(Guid apiCredentialId, string logicalModel, DateTimeOffset nowUtc)
        => TryAcquireAsync(apiCredentialId, logicalModel, null, null, nowUtc).AsTask().GetAwaiter().GetResult();

    public ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid apiCredentialId,
        string logicalModel,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
        => TryAcquireAsync(apiCredentialId, logicalModel, null, null, nowUtc, cancellationToken);

    public async ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid apiCredentialId,
        string logicalModel,
        string? ownerTenantId,
        string? ownerObjectId,
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
        var credentialPolicy = ResolvePolicy(apiCredentialId, logicalModel);
        var userPolicy = ResolveUserPolicy(ownerTenantId, ownerObjectId, logicalModel);
        if (credentialPolicy is null && userPolicy is null)
        {
            activity?.SetTag("llmproxy.rate_limit.result", "no_policy");
            return RateLimitDecision.Permit();
        }

        var policies = new List<RateLimitPolicySnapshot>(2);
        if (userPolicy is not null)
        {
            policies.Add(userPolicy);
            LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.user_policy_id", userPolicy.Id);
        }
        if (credentialPolicy is not null)
        {
            policies.Add(credentialPolicy);
            LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.credential_policy_id", credentialPolicy.Id);
        }

        activity?.SetTag("llmproxy.rate_limit.policy_count", policies.Count);
        var counterDecision = await counterStore.TryAcquireAsync(
            policies.Select(policy => new RateLimitCounterRequest(
                policy.Id,
                policy.RequestsPerWindow,
                policy.WindowSeconds)).ToArray(),
            nowUtc,
            cancellationToken);

        activity?.SetTag("llmproxy.rate_limit.provider", counterDecision.Provider);
        activity?.SetTag("llmproxy.rate_limit.degraded", counterDecision.Degraded);
        activity?.SetTag("llmproxy.rate_limit.window_count", counterDecision.WindowCount);

        if (!counterDecision.Allowed)
        {
            var rejectedPolicy = policies.FirstOrDefault(policy => policy.Id == counterDecision.RejectedPolicyId)
                ?? userPolicy
                ?? credentialPolicy!;
            activity?.SetTag("llmproxy.rate_limit.result", "rejected");
            activity?.SetTag("llmproxy.rate_limit.scope", rejectedPolicy.IsUserScoped ? "user" : "credential");
            activity?.SetTag("llmproxy.rate_limit.retry_after_seconds", counterDecision.RetryAfterSeconds);
            LlmProxyActivity.MarkError(activity, "rate_limit_exceeded");
            return RateLimitDecision.Reject(rejectedPolicy, counterDecision.RetryAfterSeconds);
        }

        activity?.SetTag("llmproxy.rate_limit.result", "allowed");
        return RateLimitDecision.Permit(credentialPolicy ?? userPolicy);
    }

    private static void AddPolicy(
        IDictionary<CredentialRateLimitKey, RateLimitPolicySnapshot> credentialPolicies,
        IDictionary<UserRateLimitKey, RateLimitPolicySnapshot> userPolicies,
        RateLimitPolicySnapshot policy)
    {
        var model = NormalizeModel(policy.LogicalModel);
        if (policy.IsUserScoped)
        {
            userPolicies[new UserRateLimitKey(
                NormalizeIdentity(policy.OwnerTenantId!),
                NormalizeIdentity(policy.OwnerObjectId!),
                model)] = policy;
            return;
        }

        if (policy.ApiCredentialId == Guid.Empty)
        {
            throw new InvalidOperationException($"Rate-limit policy '{policy.Id}' has no valid credential or user scope.");
        }

        credentialPolicies[new CredentialRateLimitKey(policy.ApiCredentialId, model)] = policy;
    }

    private static string NormalizeModel(string? logicalModel) =>
        string.IsNullOrWhiteSpace(logicalModel)
            ? string.Empty
            : logicalModel.Trim().ToUpperInvariant();

    private static string NormalizeIdentity(string value) => value.Trim().ToUpperInvariant();

    private readonly record struct CredentialRateLimitKey(Guid ApiCredentialId, string LogicalModel);
    private readonly record struct UserRateLimitKey(string TenantId, string ObjectId, string LogicalModel);
}
