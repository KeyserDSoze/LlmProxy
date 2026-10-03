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
    string? OwnerObjectId = null,
    Guid? UsageGroupId = null)
{
    public bool HasOutputTokenBudget => OutputTokensPerWindow.HasValue && MaxOutputTokensPerRequest.HasValue;
    public bool IsUserScoped =>
        ApiCredentialId == Guid.Empty &&
        !string.IsNullOrWhiteSpace(OwnerTenantId) &&
        !string.IsNullOrWhiteSpace(OwnerObjectId);
    public bool IsUsageGroupScoped =>
        ApiCredentialId == Guid.Empty &&
        string.IsNullOrWhiteSpace(OwnerTenantId) &&
        string.IsNullOrWhiteSpace(OwnerObjectId) &&
        UsageGroupId is Guid groupId &&
        groupId != Guid.Empty;
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

    private IReadOnlyDictionary<UsageGroupRateLimitKey, RateLimitPolicySnapshot> _usageGroupPolicies =
        new Dictionary<UsageGroupRateLimitKey, RateLimitPolicySnapshot>();

    public void ReplacePolicies(IEnumerable<RateLimitPolicySnapshot> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var credentials = new Dictionary<CredentialRateLimitKey, RateLimitPolicySnapshot>();
        var users = new Dictionary<UserRateLimitKey, RateLimitPolicySnapshot>();
        var groups = new Dictionary<UsageGroupRateLimitKey, RateLimitPolicySnapshot>();

        foreach (var policy in policies.Where(policy => policy.Enabled))
        {
            AddPolicy(credentials, users, groups, policy);
        }

        Volatile.Write(ref _credentialPolicies, credentials);
        Volatile.Write(ref _userPolicies, users);
        Volatile.Write(ref _usageGroupPolicies, groups);
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
        var groupNext = Volatile.Read(ref _usageGroupPolicies)
            .Where(item => item.Value.Id != policy.Id)
            .ToDictionary(item => item.Key, item => item.Value);

        if (policy.Enabled)
        {
            AddPolicy(credentialNext, userNext, groupNext, policy);
        }

        Volatile.Write(ref _credentialPolicies, credentialNext);
        Volatile.Write(ref _userPolicies, userNext);
        Volatile.Write(ref _usageGroupPolicies, groupNext);
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
        var groupNext = Volatile.Read(ref _usageGroupPolicies)
            .Where(item => item.Value.Id != policyId)
            .ToDictionary(item => item.Key, item => item.Value);

        Volatile.Write(ref _credentialPolicies, credentialNext);
        Volatile.Write(ref _userPolicies, userNext);
        Volatile.Write(ref _usageGroupPolicies, groupNext);
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

    public RateLimitPolicySnapshot? ResolveUsageGroupPolicy(Guid? usageGroupId, string logicalModel)
    {
        if (usageGroupId is not Guid groupId ||
            groupId == Guid.Empty ||
            string.IsNullOrWhiteSpace(logicalModel))
        {
            return null;
        }

        var policies = Volatile.Read(ref _usageGroupPolicies);
        var normalizedModel = NormalizeModel(logicalModel);
        return policies.TryGetValue(new UsageGroupRateLimitKey(groupId, normalizedModel), out var exact)
            ? exact
            : policies.TryGetValue(new UsageGroupRateLimitKey(groupId, string.Empty), out var fallback)
                ? fallback
                : null;
    }

    public IReadOnlyList<RateLimitPolicySnapshot> ResolveApplicablePolicies(
        Guid apiCredentialId,
        string logicalModel,
        string? ownerTenantId,
        string? ownerObjectId,
        Guid? usageGroupId,
        bool enforceCallerGovernance)
    {
        if (!enforceCallerGovernance ||
            apiCredentialId == Guid.Empty ||
            string.IsNullOrWhiteSpace(logicalModel))
        {
            return Array.Empty<RateLimitPolicySnapshot>();
        }

        var resolved = new List<RateLimitPolicySnapshot>(3);
        var userPolicy = ResolveUserPolicy(ownerTenantId, ownerObjectId, logicalModel);
        var groupPolicy = ResolveUsageGroupPolicy(usageGroupId, logicalModel);
        var credentialPolicy = ResolvePolicy(apiCredentialId, logicalModel);

        if (userPolicy is not null) resolved.Add(userPolicy);
        if (groupPolicy is not null) resolved.Add(groupPolicy);
        if (credentialPolicy is not null) resolved.Add(credentialPolicy);
        return resolved;
    }

    public RateLimitDecision TryAcquire(Guid apiCredentialId, string logicalModel, DateTimeOffset nowUtc)
        => TryAcquireAsync(
            apiCredentialId,
            logicalModel,
            null,
            null,
            null,
            enforceCallerGovernance: true,
            nowUtc).AsTask().GetAwaiter().GetResult();

    public ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid apiCredentialId,
        string logicalModel,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
        => TryAcquireAsync(
            apiCredentialId,
            logicalModel,
            null,
            null,
            null,
            enforceCallerGovernance: true,
            nowUtc,
            cancellationToken);

    public ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid apiCredentialId,
        string logicalModel,
        string? ownerTenantId,
        string? ownerObjectId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
        => TryAcquireAsync(
            apiCredentialId,
            logicalModel,
            ownerTenantId,
            ownerObjectId,
            null,
            enforceCallerGovernance: true,
            nowUtc,
            cancellationToken);

    public async ValueTask<RateLimitDecision> TryAcquireAsync(
        Guid apiCredentialId,
        string logicalModel,
        string? ownerTenantId,
        string? ownerObjectId,
        Guid? usageGroupId,
        bool enforceCallerGovernance,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        using var activity = LlmProxyActivity.Start("llmproxy.governance.rate_limit");
        LlmProxyActivity.SetGuid(activity, "llmproxy.api_credential.id", apiCredentialId);
        activity?.SetTag("llmproxy.logical_model", logicalModel);
        activity?.SetTag("llmproxy.rate_limit.caller_governance", enforceCallerGovernance);

        if (!enforceCallerGovernance || apiCredentialId == Guid.Empty)
        {
            activity?.SetTag("llmproxy.rate_limit.result", "not_applicable");
            return RateLimitDecision.Permit();
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(logicalModel);
        var policies = ResolveApplicablePolicies(
            apiCredentialId,
            logicalModel,
            ownerTenantId,
            ownerObjectId,
            usageGroupId,
            enforceCallerGovernance);

        if (policies.Count == 0)
        {
            activity?.SetTag("llmproxy.rate_limit.result", "no_policy");
            return RateLimitDecision.Permit();
        }

        foreach (var policy in policies)
        {
            if (policy.IsUserScoped)
            {
                LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.user_policy_id", policy.Id);
            }
            else if (policy.IsUsageGroupScoped)
            {
                LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.group_policy_id", policy.Id);
            }
            else
            {
                LlmProxyActivity.SetGuid(activity, "llmproxy.rate_limit.credential_policy_id", policy.Id);
            }
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
                ?? policies[0];
            activity?.SetTag("llmproxy.rate_limit.result", "rejected");
            activity?.SetTag("llmproxy.rate_limit.scope", Scope(rejectedPolicy));
            activity?.SetTag("llmproxy.rate_limit.retry_after_seconds", counterDecision.RetryAfterSeconds);
            LlmProxyActivity.MarkError(activity, "rate_limit_exceeded");
            return RateLimitDecision.Reject(rejectedPolicy, counterDecision.RetryAfterSeconds);
        }

        activity?.SetTag("llmproxy.rate_limit.result", "allowed");
        return RateLimitDecision.Permit(policies[^1]);
    }

    private static void AddPolicy(
        IDictionary<CredentialRateLimitKey, RateLimitPolicySnapshot> credentialPolicies,
        IDictionary<UserRateLimitKey, RateLimitPolicySnapshot> userPolicies,
        IDictionary<UsageGroupRateLimitKey, RateLimitPolicySnapshot> usageGroupPolicies,
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

        if (policy.IsUsageGroupScoped)
        {
            usageGroupPolicies[new UsageGroupRateLimitKey(policy.UsageGroupId!.Value, model)] = policy;
            return;
        }

        if (policy.ApiCredentialId == Guid.Empty)
        {
            throw new InvalidOperationException($"Rate-limit policy '{policy.Id}' has no valid credential, user or usage-group scope.");
        }

        credentialPolicies[new CredentialRateLimitKey(policy.ApiCredentialId, model)] = policy;
    }

    private static string Scope(RateLimitPolicySnapshot policy) =>
        policy.IsUserScoped ? "user" :
        policy.IsUsageGroupScoped ? "usage_group" :
        "credential";

    private static string NormalizeModel(string? logicalModel) =>
        string.IsNullOrWhiteSpace(logicalModel)
            ? string.Empty
            : logicalModel.Trim().ToUpperInvariant();

    private static string NormalizeIdentity(string value) => value.Trim().ToUpperInvariant();

    private readonly record struct CredentialRateLimitKey(Guid ApiCredentialId, string LogicalModel);
    private readonly record struct UserRateLimitKey(string TenantId, string ObjectId, string LogicalModel);
    private readonly record struct UsageGroupRateLimitKey(Guid UsageGroupId, string LogicalModel);
}
