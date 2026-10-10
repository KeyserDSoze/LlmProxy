using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace LlmProxy.Api.Product;

public sealed class ReleaseDiscoveryService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IConfiguration configuration)
{
    private readonly string _repository = configuration["Updates:Repository"] ?? "KeyserDSoze/LlmProxy";

    /// <summary>
    /// Lightweight, cached whitelist of stable release versions that really carry
    /// both Linux Agent architectures, adjacent SHA-256 and public pairing script.
    /// Used by anonymous download endpoints without evaluating update plans.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDownloadableAgentVersionsAsync(CancellationToken token)
    {
        var key = $"downloadable-agent-releases:{_repository}";
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null)
            return cached;

        var client = httpClientFactory.CreateClient("github-releases");
        var entries = await client.GetFromJsonAsync<GitHubRelease[]>(
            $"https://api.github.com/repos/{_repository}/releases?per_page=100", token) ?? [];
        var versions = entries.Where(x => !x.Draft && !x.Prerelease)
            .Select(x => new { Version = NormalizeVersion(x.TagName),
                Assets = x.Assets?.Select(a => a.Name).ToHashSet(StringComparer.Ordinal) })
            .Where(x => x.Version is not null && x.Assets is not null &&
                x.Assets.Contains("llmproxy-connect-node.sh") &&
                x.Assets.Contains("llmproxy-connect-node.sh.sha256") &&
                new[] { "linux-x64", "linux-arm64" }.All(arch =>
                    x.Assets.Contains($"llmproxy-node-agent-{x.Version}-{arch}.tar.gz") &&
                    x.Assets.Contains($"llmproxy-node-agent-{x.Version}-{arch}.tar.gz.sha256")))
            .Select(x => x.Version!)
            .OrderByDescending(ParseVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        cache.Set(key, versions, TimeSpan.FromMinutes(5));
        return versions;
    }

    public async Task<IReadOnlyList<AvailableProductRelease>> GetAvailableAsync(
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"product-releases:{_repository}:{currentVersion}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<AvailableProductRelease>? cached) && cached is not null)
        {
            return cached;
        }

        var client = httpClientFactory.CreateClient("github-releases");
        var releases = await LoadReleasesAsync(client, cancellationToken);

        var stable = releases
            .Where(item => !item.Draft && !item.Prerelease)
            .Select(item => (Release: item, Version: NormalizeVersion(item.TagName)))
            .Where(item => item.Version is not null)
            .ToArray();

        var plans = await Task.WhenAll(stable.Select(async item =>
        {
            var plan = await ReadPlanAsync(client, item.Release, cancellationToken);
            return new AvailableProductRelease(
                item.Version!,
                item.Release.Name ?? $"LlmProxy {item.Version}",
                item.Release.PublishedAt,
                item.Release.HtmlUrl,
                IsNewer(item.Version!, currentVersion),
                plan.Mode,
                plan.Title,
                plan.Description,
                plan.RequiresHostRestart,
                (plan.OperatorCommand ?? "sudo -E llmproxyctl update {version}").Replace("{version}", item.Version!, StringComparison.Ordinal));
        }));

        var result = plans
            .OrderByDescending(item => ParseVersion(item.Version))
            .ToArray();

        cache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        return result;
    }

    private async Task<IReadOnlyList<GitHubRelease>> LoadReleasesAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        const int maximumPages = 20;
        var releases = new List<GitHubRelease>();

        for (var page = 1; page <= maximumPages; page++)
        {
            var batch = await client.GetFromJsonAsync<GitHubRelease[]>(
                $"https://api.github.com/repos/{_repository}/releases?per_page={pageSize}&page={page}",
                cancellationToken) ?? [];
            releases.AddRange(batch);
            if (batch.Length < pageSize)
            {
                return releases;
            }
        }

        throw new InvalidOperationException(
            $"Release discovery exceeded {maximumPages * pageSize} releases. Refusing to construct a potentially incomplete upgrade chain.");
    }

    private static async Task<ReleaseUpdatePlan> ReadPlanAsync(
        HttpClient client,
        GitHubRelease release,
        CancellationToken cancellationToken)
    {
        var asset = release.Assets.FirstOrDefault(item =>
            string.Equals(item.Name, "llmproxy-update-plan.json", StringComparison.OrdinalIgnoreCase));
        if (asset?.BrowserDownloadUrl is null)
        {
            return ReleaseUpdatePlan.LegacyDefault;
        }

        try
        {
            return await client.GetFromJsonAsync<ReleaseUpdatePlan>(asset.BrowserDownloadUrl, cancellationToken)
                ?? ReleaseUpdatePlan.LegacyDefault;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ReleaseUpdatePlan.LegacyDefault;
        }
    }

    private static string? NormalizeVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v');
        return Version.TryParse(value, out var parsed) && parsed.Build >= 0 ? parsed.ToString(3) : null;
    }

    private static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var candidateVersion) &&
        (!Version.TryParse(current, out var currentVersion) || candidateVersion > currentVersion);

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var parsed) ? parsed : new Version();

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        string? Name,
        bool Draft,
        bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        string Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}

public sealed record ReleaseUpdatePlan(
    int SchemaVersion,
    string Mode,
    string Title,
    string Description,
    bool RequiresHostRestart,
    string? OperatorCommand)
{
    public static ReleaseUpdatePlan LegacyDefault { get; } = new(
        1,
        "standard",
        "Standard immutable update",
        "Uses the versioned LlmProxy installer. This release predates explicit update-plan metadata.",
        false,
        "sudo -E llmproxyctl update {version}");
}

public sealed record AvailableProductRelease(
    string Version,
    string Title,
    DateTimeOffset PublishedAtUtc,
    string ReleaseUrl,
    bool IsNewer,
    string UpdateMode,
    string UpdateTitle,
    string UpdateDescription,
    bool RequiresHostRestart,
    string OperatorCommand);
