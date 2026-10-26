using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Read-only, bounded access to the public Hugging Face Hub. No user-supplied
/// domains, arbitrary URL fetching, image builds, or trusted-install assumptions.
/// Cache discovery to avoid Hub rate limits in multi-user Admin sessions.
/// </summary>
public sealed class HuggingFaceDiscoveryService(IHttpClientFactory clients, IMemoryCache cache)
{
    public sealed record HubModel(
        string Id, string? Revision, DateTimeOffset? LastModified,
        long Downloads, long Likes, double? TrendingScore,
        string? PipelineTag, bool Gated, bool Private, string? License,
        string Url, bool HasGguf = false);

    public sealed record RevisionStatus(
        string Id, string? CachedRevision, string? LatestRevision,
        bool? UpdatedUpstream, string Message, DateTimeOffset? LastModified);

    public static bool ValidRepository(string id) =>
        id.Length is > 2 and <= 200 && Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,98}/[A-Za-z0-9][A-Za-z0-9_.-]{0,98}$");

    public static string BuildDiscoverUrl(string search, string sort, int limit)
    {
        var sortKey = sort switch { "downloads" => "downloads", "recent" => "lastModified", _ => "trendingScore" };
        return "https://huggingface.co/api/models?" +
            (string.IsNullOrWhiteSpace(search) ? "pipeline_tag=text-generation&" : "") +
            "limit=" + limit + "&sort=" + sortKey + "&direction=-1" +
            (string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim()));
    }

    public async Task<IReadOnlyList<HubModel>> DiscoverAsync(string search, string sort,
        int limit, CancellationToken token)
    {
        if (limit is < 1 or > 40 || search.Length > 100 ||
            sort is not ("trending" or "downloads" or "recent") ||
            search.Any(ch => char.IsControl(ch)))
            throw new ArgumentException("Invalid Hugging Face search or sorting parameters.");
        // Non-empty search includes multimodal (image-text-to-text) and GGUF repositories.
        var uri = BuildDiscoverUrl(search, sort, limit);
        var key = "huggingface:search:" + uri;
        var rows = await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            using var response = await clients.CreateClient("huggingface-hub").GetAsync(uri, token);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Hub discovery response must contain a model list.");
            var result = new List<HubModel>();
            foreach (var item in doc.RootElement.EnumerateArray().Take(40))
                if (Parse(item) is { } model && !model.Private)
                    result.Add(model);
            return (IReadOnlyList<HubModel>)result;
        });
        // Exact org/model lookup bypasses ranking, format/task filters and
        // short lists. ID is strictly validated: no arbitrary host or URL.
        if (ValidRepository(search.Trim()))
        {
            var exact = await LatestAsync(search.Trim(), token);
            if (exact is { Private: false })
                return new[] { exact }.Concat((rows ?? [])
                    .Where(model => !string.Equals(model.Id, exact.Id, StringComparison.OrdinalIgnoreCase)))
                    .Take(limit).ToArray();
        }
        return rows ?? [];
    }

    public async Task<HubModel?> LatestAsync(string repoId, CancellationToken token)
    {
        if (!ValidRepository(repoId)) throw new ArgumentException("Invalid model repository ID.");
        var key = "huggingface:revision:" + repoId;
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            using var response = await clients.CreateClient("huggingface-hub")
                .GetAsync("https://huggingface.co/api/models/" + repoId, token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            return Parse(doc.RootElement);
        });
    }

    public async Task<RevisionStatus> CompareAsync(string id, string? cachedRevision, CancellationToken token)
    {
        var latest = await LatestAsync(id, token);
        if (latest is null)
            return new(id, cachedRevision, null, null, "Repository is not currently available.", null);
        if (latest.Gated || latest.Private)
            return new(id, cachedRevision, latest.Revision, null,
                "Model requires access approval or is private. No automatic upgrade.", latest.LastModified);
        if (string.IsNullOrWhiteSpace(cachedRevision))
            return new(id, null, latest.Revision, null,
                "Legacy/untracked install: cached model revision is unknown; cannot safely declare an update.", latest.LastModified);
        if (string.IsNullOrWhiteSpace(latest.Revision))
            return new(id, cachedRevision, null, null, "Hub did not provide a revision SHA.", latest.LastModified);
        var changed = !string.Equals(cachedRevision, latest.Revision, StringComparison.OrdinalIgnoreCase);
        return new(id, cachedRevision, latest.Revision, changed,
            changed ? "A newer or changed repository commit exists; inspect the upstream changelog before replacing this model."
                : "Cached repository revision matches the latest Hub commit.", latest.LastModified);
    }

    private static HubModel? Parse(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty("id", out var idValue) ||
            idValue.ValueKind != JsonValueKind.String) return null;
        var id = idValue.GetString() ?? "";
        if (!ValidRepository(id)) return null;
        string? str(string property) => obj.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long num(string property) => obj.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        bool flag(string property) => obj.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.True;
        bool gated = obj.TryGetProperty("gated", out var gatedValue) &&
            (gatedValue.ValueKind == JsonValueKind.True ||
             gatedValue.ValueKind == JsonValueKind.String &&
             gatedValue.ToString() is not ("false" or ""));
        string? license = null;
        var hasGguf = false;
        if (obj.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            var values = tags.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()).ToArray();
            license = values.FirstOrDefault(x => x?.StartsWith("license:", StringComparison.Ordinal) == true)?["license:".Length..];
            hasGguf = values.Any(x => string.Equals(x, "gguf", StringComparison.OrdinalIgnoreCase));
        }
        DateTimeOffset? last = DateTimeOffset.TryParse(str("lastModified"), out var parsed) ? parsed : null;
        double? trending = obj.TryGetProperty("trendingScore", out var score) &&
            score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var d) ? d : null;
        return new(id, str("sha"), last, num("downloads"), num("likes"), trending,
            str("pipeline_tag"), gated, flag("private"), license, "https://huggingface.co/" + id, hasGguf);
    }
}
