using LlmProxy.Domain.Security;

namespace LlmProxy.Application.Security;

/// <param name="AllowedOrigins">Site-wide origins allowed for every API key (normalized, may contain "*").</param>
/// <param name="MaxAgeSeconds">How long browsers may cache a successful preflight answer.</param>
public sealed record CorsSettings(IReadOnlyList<string> AllowedOrigins, int MaxAgeSeconds)
{
    public const int DefaultMaxAgeSeconds = 600;
    public const int MaxAgeLimitSeconds = 86400;

    public static CorsSettings Default { get; } = new([], DefaultMaxAgeSeconds);

    public static int ClampMaxAge(int? seconds) =>
        Math.Clamp(seconds ?? DefaultMaxAgeSeconds, 0, MaxAgeLimitSeconds);
}

/// <summary>
/// In-memory view of the administrator CORS settings so the request path never touches PostgreSQL.
/// Replicas converge through the periodic refresh of the persisted singleton.
/// </summary>
public sealed class CorsPolicyState
{
    private CorsSettings _current = CorsSettings.Default;

    public CorsSettings Current => Volatile.Read(ref _current);

    public void Replace(CorsSettings settings) => Volatile.Write(ref _current, settings);

    public bool GlobalAllows(string normalizedOrigin) => OriginPolicy.Matches(Current.AllowedOrigins, normalizedOrigin);

    /// <summary>Site-wide list plus the credential's own list.</summary>
    public bool Allows(string normalizedOrigin, IReadOnlyList<string>? credentialOrigins) =>
        GlobalAllows(normalizedOrigin) || OriginPolicy.Matches(credentialOrigins, normalizedOrigin);
}
