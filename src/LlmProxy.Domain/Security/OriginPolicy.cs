namespace LlmProxy.Domain.Security;

/// <summary>
/// Browser origins (scheme://host[:port]) that may call the inference API directly via CORS.
/// Entries are normalized to the exact form browsers send in the Origin header; "*" allows any origin.
/// </summary>
public static class OriginPolicy
{
    public const string Wildcard = "*";
    public const int MaxOrigins = 50;
    public const int MaxOriginLength = 200;

    public static bool TryNormalize(string? raw, out string origin)
    {
        origin = string.Empty;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaxOriginLength) return false;
        if (value == Wildcard)
        {
            origin = Wildcard;
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/") return false;

        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        origin = $"{uri.Scheme}://{host.ToLowerInvariant()}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}";
        return true;
    }

    /// <summary>Validates and de-duplicates a list; throws <see cref="ArgumentException"/> naming the first bad entry.</summary>
    public static IReadOnlyList<string> NormalizeList(IEnumerable<string>? values)
    {
        var result = new List<string>();
        foreach (var raw in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!TryNormalize(raw, out var origin))
            {
                throw new ArgumentException(
                    $"'{Truncate(raw)}' is not a valid origin. Use scheme://host[:port] without a path (for example https://app.example.com), or * for any origin.");
            }

            if (!result.Contains(origin, StringComparer.Ordinal)) result.Add(origin);
        }

        if (result.Count > MaxOrigins)
        {
            throw new ArgumentException($"At most {MaxOrigins} origins are allowed.");
        }

        return result;
    }

    public static IReadOnlyList<string> ParseStored(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? ToStored(IReadOnlyList<string> origins) => origins.Count == 0 ? null : string.Join('\n', origins);

    /// <summary>True when the request origin matches any allowed entry (exact, case-insensitive, or "*").</summary>
    public static bool Matches(IEnumerable<string>? allowed, string normalizedOrigin)
    {
        if (allowed is null) return false;
        foreach (var entry in allowed)
        {
            if (entry == Wildcard || string.Equals(entry, normalizedOrigin, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string Truncate(string value) => value.Length <= 60 ? value : value[..60] + "…";
}
