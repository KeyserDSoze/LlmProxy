namespace LlmProxy.Domain.Security;

public sealed class ApiCredential
{
    private ApiCredential()
    {
    }

    public ApiCredential(string name, string keyPrefix, string keyHash, DateTimeOffset? expiresAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Credential name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            throw new ArgumentException("Key prefix is required.", nameof(keyPrefix));
        }

        if (string.IsNullOrWhiteSpace(keyHash))
        {
            throw new ArgumentException("Key hash is required.", nameof(keyHash));
        }

        Name = name.Trim();
        KeyPrefix = keyPrefix;
        KeyHash = keyHash;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string KeyPrefix { get; private set; } = string.Empty;
    public string KeyHash { get; private set; } = string.Empty;
    public bool Enabled { get; private set; } = true;
    public DateTimeOffset CreatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public DateTimeOffset? LastUsedAtUtc { get; private set; }
    public Guid? UsageGroupId { get; private set; }

    public bool IsUsable(DateTimeOffset nowUtc)
        => Enabled && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);

    public void Revoke() => Enabled = false;

    public void Rotate(string keyPrefix, string keyHash)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Revoked credentials cannot be rotated.");
        }

        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            throw new ArgumentException("Key prefix is required.", nameof(keyPrefix));
        }

        if (string.IsNullOrWhiteSpace(keyHash))
        {
            throw new ArgumentException("Key hash is required.", nameof(keyHash));
        }

        KeyPrefix = keyPrefix;
        KeyHash = keyHash;
    }

    public void AssignUsageGroup(Guid usageGroupId)
    {
        if (usageGroupId == Guid.Empty)
        {
            throw new ArgumentException("Usage group id is required.", nameof(usageGroupId));
        }

        UsageGroupId = usageGroupId;
    }

    public void ClearUsageGroup() => UsageGroupId = null;

    public void Touch(DateTimeOffset nowUtc)
    {
        if (LastUsedAtUtc is null || nowUtc - LastUsedAtUtc >= TimeSpan.FromMinutes(15))
        {
            LastUsedAtUtc = nowUtc;
        }
    }
}
