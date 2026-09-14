using LlmProxy.Domain.Security;

namespace LlmProxy.Application.Abstractions;

public sealed record ApiCredentialSnapshot(
    Guid Id,
    string KeyHash,
    bool Enabled,
    DateTimeOffset? ExpiresAtUtc,
    Guid? UsageGroupId)
{
    public bool IsUsable(DateTimeOffset nowUtc)
        => Enabled && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);

    public static ApiCredentialSnapshot From(ApiCredential credential)
        => new(
            credential.Id,
            credential.KeyHash,
            credential.Enabled,
            credential.ExpiresAtUtc,
            credential.UsageGroupId);
}

public interface IApiCredentialCache
{
    bool TryGetUsableByHash(string keyHash, DateTimeOffset nowUtc, out ApiCredentialSnapshot credential);
    void Replace(IEnumerable<ApiCredentialSnapshot> credentials);
    void Upsert(ApiCredentialSnapshot credential);
    void Remove(Guid credentialId);
}
