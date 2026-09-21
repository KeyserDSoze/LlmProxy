namespace LlmProxy.Domain.Security;

public sealed class ApiCredential
{
    private ApiCredential()
    {
    }

    public ApiCredential(
        string name,
        string keyPrefix,
        string keyHash,
        DateTimeOffset? expiresAtUtc = null,
        string? ownerTenantId = null,
        string? ownerObjectId = null,
        string? ownerPrincipalName = null)
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

        var hasTenant = !string.IsNullOrWhiteSpace(ownerTenantId);
        var hasObject = !string.IsNullOrWhiteSpace(ownerObjectId);
        if (hasTenant != hasObject)
        {
            throw new ArgumentException("Credential ownership requires both Entra tenant id and object id.");
        }

        Name = name.Trim();
        KeyPrefix = keyPrefix;
        KeyHash = keyHash;
        ExpiresAtUtc = expiresAtUtc;
        OwnerTenantId = hasTenant ? ownerTenantId!.Trim() : null;
        OwnerObjectId = hasObject ? ownerObjectId!.Trim() : null;
        OwnerPrincipalName = string.IsNullOrWhiteSpace(ownerPrincipalName) ? null : ownerPrincipalName.Trim();
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
    public string? OwnerTenantId { get; private set; }
    public string? OwnerObjectId { get; private set; }
    public string? OwnerPrincipalName { get; private set; }

    public bool IsPersonal => OwnerTenantId is not null && OwnerObjectId is not null;

    public bool IsUsable(DateTimeOffset nowUtc)
        => Enabled && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);

    public bool IsOwnedBy(string tenantId, string objectId)
        => IsPersonal &&
           string.Equals(OwnerTenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(OwnerObjectId, objectId, StringComparison.OrdinalIgnoreCase);

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
