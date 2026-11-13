using LlmProxy.Domain.Security;

namespace LlmProxy.Application.Abstractions;

public sealed record ApiCredentialSnapshot(
    Guid Id,
    string KeyHash,
    bool Enabled,
    DateTimeOffset? ExpiresAtUtc,
    Guid? UsageGroupId,
    string? OwnerTenantId = null,
    string? OwnerObjectId = null,
    bool EnforceCallerGovernance = false,
    IReadOnlyList<string>? AllowedOrigins = null)
{
    public bool IsUsable(DateTimeOffset nowUtc)
        => Enabled && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);

    public static ApiCredentialSnapshot From(ApiCredential credential)
        => new(
            credential.Id,
            credential.KeyHash,
            credential.Enabled,
            credential.ExpiresAtUtc,
            credential.UsageGroupId,
            credential.OwnerTenantId,
            credential.OwnerObjectId,
            credential.EnforceCallerGovernance,
            credential.AllowedOrigins is { Count: > 0 } origins ? origins : null);
}

public interface IApiCredentialCache
{
    bool TryGetUsableByHash(string keyHash, DateTimeOffset nowUtc, out ApiCredentialSnapshot credential);

    /// <summary>True when any enabled credential lists the origin; answers unauthenticated CORS preflights.</summary>
    bool AnyAllowsOrigin(string normalizedOrigin);
    void Replace(IEnumerable<ApiCredentialSnapshot> credentials);
    void Upsert(ApiCredentialSnapshot credential);
    void Remove(Guid credentialId);
}
