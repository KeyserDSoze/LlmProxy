namespace LlmProxy.Api.SystemOne;

/// <summary>Never route a missing/disabled default; an absent choice has a deterministic fallback.</summary>
public static class SystemOneDefaultResolver
{
    public static string? Resolve(IEnumerable<string> availableModels,
        string? persistedDefault, string? configurationDefault)
    {
        var available = availableModels
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (available.Length == 0) return null;
        if (!string.IsNullOrWhiteSpace(persistedDefault) &&
            available.Contains(persistedDefault.Trim(), StringComparer.Ordinal))
            return persistedDefault.Trim();
        if (!string.IsNullOrWhiteSpace(configurationDefault) &&
            available.Contains(configurationDefault.Trim(), StringComparer.Ordinal))
            return configurationDefault.Trim();
        return available.Contains("laya-english-cpu", StringComparer.Ordinal)
            ? "laya-english-cpu" : available[0];
    }
}
