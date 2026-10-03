namespace LlmProxy.Domain.Governance;

public sealed class UsageGroupRateLimitPolicy
{
    private UsageGroupRateLimitPolicy()
    {
    }

    public UsageGroupRateLimitPolicy(
        Guid usageGroupId,
        string? logicalModel,
        int requestsPerWindow,
        int windowSeconds = 60,
        bool enabled = true,
        int? outputTokensPerWindow = null,
        int? maxOutputTokensPerRequest = null)
    {
        if (usageGroupId == Guid.Empty)
        {
            throw new ArgumentException("Usage group id is required.", nameof(usageGroupId));
        }

        UsageGroupId = usageGroupId;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Update(logicalModel, requestsPerWindow, windowSeconds, enabled);

        if (outputTokensPerWindow.HasValue || maxOutputTokensPerRequest.HasValue)
        {
            if (!outputTokensPerWindow.HasValue || !maxOutputTokensPerRequest.HasValue)
            {
                throw new ArgumentException("OutputTokensPerWindow and MaxOutputTokensPerRequest must either both be set or both be null.");
            }

            SetOutputTokenBudget(outputTokensPerWindow.Value, maxOutputTokensPerRequest.Value);
        }
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UsageGroupId { get; private set; }
    public string? LogicalModel { get; private set; }
    public int RequestsPerWindow { get; private set; }
    public int WindowSeconds { get; private set; }
    public bool Enabled { get; private set; }
    public int? OutputTokensPerWindow { get; private set; }
    public int? MaxOutputTokensPerRequest { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void Update(string? logicalModel, int requestsPerWindow, int windowSeconds, bool enabled)
    {
        if (requestsPerWindow is < 1 or > RateLimitPolicy.MaxRequestsPerWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(requestsPerWindow));
        }

        if (windowSeconds is < 1 or > RateLimitPolicy.MaxWindowSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        }

        var normalizedModel = string.IsNullOrWhiteSpace(logicalModel) ? null : logicalModel.Trim();
        if (normalizedModel?.Length > 160)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalModel));
        }

        LogicalModel = normalizedModel;
        RequestsPerWindow = requestsPerWindow;
        WindowSeconds = windowSeconds;
        Enabled = enabled;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetOutputTokenBudget(int outputTokensPerWindow, int maxOutputTokensPerRequest)
    {
        if (outputTokensPerWindow is < 1 or > RateLimitPolicy.MaxOutputTokensPerWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokensPerWindow));
        }

        if (maxOutputTokensPerRequest is < 1 or > RateLimitPolicy.MaxOutputTokensPerRequestLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokensPerRequest));
        }

        if (maxOutputTokensPerRequest > outputTokensPerWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokensPerRequest));
        }

        OutputTokensPerWindow = outputTokensPerWindow;
        MaxOutputTokensPerRequest = maxOutputTokensPerRequest;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void ClearOutputTokenBudget()
    {
        OutputTokensPerWindow = null;
        MaxOutputTokensPerRequest = null;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
