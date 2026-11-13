using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Security;

namespace LlmProxy.Infrastructure.Security;

public sealed class InMemoryApiCredentialCache : IApiCredentialCache
{
    private sealed record CacheState(
        Dictionary<string, ApiCredentialSnapshot> ByHash,
        Dictionary<Guid, string> HashById,
        string[] AllowedOrigins);

    private readonly object _gate = new();
    private CacheState _state = new(
        new Dictionary<string, ApiCredentialSnapshot>(StringComparer.Ordinal),
        new Dictionary<Guid, string>(),
        []);

    public bool TryGetUsableByHash(
        string keyHash,
        DateTimeOffset nowUtc,
        out ApiCredentialSnapshot credential)
    {
        var state = Volatile.Read(ref _state);
        if (state.ByHash.TryGetValue(keyHash, out var resolved) && resolved.IsUsable(nowUtc))
        {
            credential = resolved;
            return true;
        }

        credential = null!;
        return false;
    }

    public bool AnyAllowsOrigin(string normalizedOrigin) =>
        OriginPolicy.Matches(Volatile.Read(ref _state).AllowedOrigins, normalizedOrigin);

    public void Replace(IEnumerable<ApiCredentialSnapshot> credentials)
    {
        var byHash = new Dictionary<string, ApiCredentialSnapshot>(StringComparer.Ordinal);
        var hashById = new Dictionary<Guid, string>();

        foreach (var credential in credentials)
        {
            if (!byHash.TryAdd(credential.KeyHash, credential))
            {
                throw new InvalidOperationException("Duplicate API credential hash detected while rebuilding the runtime cache.");
            }

            if (!hashById.TryAdd(credential.Id, credential.KeyHash))
            {
                throw new InvalidOperationException($"Duplicate API credential id '{credential.Id}' detected while rebuilding the runtime cache.");
            }
        }

        lock (_gate)
        {
            Volatile.Write(ref _state, new CacheState(byHash, hashById, IndexOrigins(byHash.Values)));
        }
    }

    public void Upsert(ApiCredentialSnapshot credential)
    {
        lock (_gate)
        {
            var current = Volatile.Read(ref _state);
            var byHash = new Dictionary<string, ApiCredentialSnapshot>(current.ByHash, StringComparer.Ordinal);
            var hashById = new Dictionary<Guid, string>(current.HashById);

            if (hashById.TryGetValue(credential.Id, out var previousHash) &&
                !StringComparer.Ordinal.Equals(previousHash, credential.KeyHash))
            {
                byHash.Remove(previousHash);
            }

            if (byHash.TryGetValue(credential.KeyHash, out var existing) && existing.Id != credential.Id)
            {
                throw new InvalidOperationException("API credential hash collision detected while updating the runtime cache.");
            }

            byHash[credential.KeyHash] = credential;
            hashById[credential.Id] = credential.KeyHash;
            Volatile.Write(ref _state, new CacheState(byHash, hashById, IndexOrigins(byHash.Values)));
        }
    }

    public void Remove(Guid credentialId)
    {
        lock (_gate)
        {
            var current = Volatile.Read(ref _state);
            if (!current.HashById.TryGetValue(credentialId, out var keyHash))
            {
                return;
            }

            var byHash = new Dictionary<string, ApiCredentialSnapshot>(current.ByHash, StringComparer.Ordinal);
            var hashById = new Dictionary<Guid, string>(current.HashById);
            byHash.Remove(keyHash);
            hashById.Remove(credentialId);
            Volatile.Write(ref _state, new CacheState(byHash, hashById, IndexOrigins(byHash.Values)));
        }
    }

    // Revoked credentials must not open preflights; expiry is judged per request.
    private static string[] IndexOrigins(IEnumerable<ApiCredentialSnapshot> credentials) =>
        credentials.Where(item => item.Enabled && item.AllowedOrigins is { Count: > 0 })
            .SelectMany(item => item.AllowedOrigins!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
