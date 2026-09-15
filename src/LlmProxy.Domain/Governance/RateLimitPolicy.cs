namespace LlmProxy.Domain.Governance;

public sealed class RateLimitPolicy
{
    public const int MaxRequestsPerWindow = 1_000_000;
    public const int MaxWindowSeconds = 86_400;
    public const int MaxOutputTokensPerWindow = 2_000_000_000;
    public const int MaxOutputTokensPerRequestLimit = 10_000_000;

    private RateLimitPolicy()
    {
    }

    public RateLimitPolicy(
        Guid apiCredentialId,
        string? logicalModel,
        int requestsPerWindow,
        int windowSeconds = 60,
        bool enabled = true,
        int? outputTokensPerWindow = null,
        int? maxOutputTokensPerRequest = null)
    {
        if (apiCredentialId == Guid.Empty)
        {
            throw new ArgumentException("API credential id is required.", nameof(apiCredentialId));
        }

        ApiCredentialId = apiCredentialId;
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
    public Guid ApiCredentialId { get; private set; }
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
        if (requestsPerWindow is < 1 or > MaxRequestsPerWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestsPerWindow),
                $"Requests per window must be between 1 and {MaxRequestsPerWindow}.");
        }

        if (windowSeconds is < 1 or > MaxWindowSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSeconds),
                $"Window seconds must be between 1 and {MaxWindowSeconds}.");
        }

        var normalizedModel = string.IsNullOrWhiteSpace(logicalModel) ? null : logicalModel.Trim();
        if (normalizedModel?.Length > 160)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalModel), "Logical model cannot exceed 160 characters.");
        }

        LogicalModel = normalizedModel;
        RequestsPerWindow = requestsPerWindow;
        WindowSeconds = windowSeconds;
        Enabled = enabled;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetOutputTokenBudget(int outputTokensPerWindow, int maxOutputTokensPerRequest)
    {
        if (outputTokensPerWindow is < 1 or > MaxOutputTokensPerWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputTokensPerWindow),
                $"Output tokens per window must be between 1 and {MaxOutputTokensPerWindow}.");
        }

        if (maxOutputTokensPerRequest is < 1 or > MaxOutputTokensPerRequestLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokensPerRequest),
                $"Max output tokens per request must be between 1 and {MaxOutputTokensPerRequestLimit}.");
        }

        if (maxOutputTokensPerRequest > outputTokensPerWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokensPerRequest),
                "Max output tokens per request cannot exceed the output-token window budget.");
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
