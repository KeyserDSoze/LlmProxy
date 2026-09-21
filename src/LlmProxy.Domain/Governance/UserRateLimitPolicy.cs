namespace LlmProxy.Domain.Governance;

public sealed class UserRateLimitPolicy
{
    private UserRateLimitPolicy()
    {
    }

    public UserRateLimitPolicy(
        string ownerTenantId,
        string ownerObjectId,
        string? logicalModel,
        int requestsPerWindow,
        int windowSeconds = 60,
        bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(ownerTenantId))
        {
            throw new ArgumentException("Owner tenant id is required.", nameof(ownerTenantId));
        }

        if (string.IsNullOrWhiteSpace(ownerObjectId))
        {
            throw new ArgumentException("Owner object id is required.", nameof(ownerObjectId));
        }

        if (ownerTenantId.Trim().Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerTenantId), "Owner tenant id cannot exceed 64 characters.");
        }

        if (ownerObjectId.Trim().Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerObjectId), "Owner object id cannot exceed 64 characters.");
        }

        OwnerTenantId = ownerTenantId.Trim();
        OwnerObjectId = ownerObjectId.Trim();
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Update(logicalModel, requestsPerWindow, windowSeconds, enabled);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string OwnerTenantId { get; private set; } = string.Empty;
    public string OwnerObjectId { get; private set; } = string.Empty;
    public string? LogicalModel { get; private set; }
    public int RequestsPerWindow { get; private set; }
    public int WindowSeconds { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void Update(string? logicalModel, int requestsPerWindow, int windowSeconds, bool enabled)
    {
        if (requestsPerWindow is < 1 or > RateLimitPolicy.MaxRequestsPerWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestsPerWindow),
                $"Requests per window must be between 1 and {RateLimitPolicy.MaxRequestsPerWindow}.");
        }

        if (windowSeconds is < 1 or > RateLimitPolicy.MaxWindowSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSeconds),
                $"Window seconds must be between 1 and {RateLimitPolicy.MaxWindowSeconds}.");
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
}
