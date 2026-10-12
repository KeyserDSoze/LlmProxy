using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Models;

namespace LlmProxy.Api.SystemOne;

/// <summary>Never route a missing/disabled default; an absent choice has a deterministic fallback.</summary>
public static class SystemOneDefaultResolver
{
    /// <summary>Only routes actually enabled in the catalog may be selected as
    /// an implicit default. Merely registering a logical alias is not enough.</summary>
    public static async Task<string[]> GetPublishedModelNamesAsync(
        IDeploymentCatalog catalog, CancellationToken token)
    {
        var models = await catalog.GetPublicModelsAsync(token, ModelSurface.SystemOne);
        var published = new List<string>();
        foreach (var model in models)
        {
            var candidates = await catalog.GetCandidatesAsync(model.PublicName, token);
            if (candidates.Any(route => route.Surface == ModelSurface.SystemOne))
                published.Add(model.PublicName);
        }
        return published.Order(StringComparer.Ordinal).ToArray();
    }

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
