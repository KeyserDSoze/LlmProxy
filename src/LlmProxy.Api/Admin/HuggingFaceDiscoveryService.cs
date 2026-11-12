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

    public sealed record GgufVariant(string Quantization, string FileName, int Parts = 1, long? SizeBytes = null);

    public sealed record GgufRepository(bool Found, bool Gated, IReadOnlyList<string> ModelFiles,
        IReadOnlyList<GgufVariant> Variants);

    private static readonly string[] GgufSidecars = ["mmproj", "mtp-", "dflash-", "eagle3-", "dspark-"];

    // Same file rules llama.cpp applies to -hf repo[:quant]: split shards and
    // subdirectories are selectable, projector/imatrix/draft sidecars are not.
    private static bool IsGgufModelFile(string path)
    {
        if (!path.EndsWith(".gguf", StringComparison.Ordinal)) return false;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return !name.Contains("imatrix", StringComparison.Ordinal) &&
               !GgufSidecars.Any(keyword => name.Contains(keyword, StringComparison.Ordinal));
    }

    private static (string Prefix, string Tag, int Index, int Count) GgufSplit(string path)
    {
        var prefix = path[..^".gguf".Length];
        int index = 1, count = 1;
        var split = Regex.Match(prefix, @"^(.+)-([0-9]{5})-of-([0-9]{5})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (split.Success)
        {
            index = int.Parse(split.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            count = int.Parse(split.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            prefix = split.Groups[1].Value;
        }
        var tag = Regex.Match(prefix, @"[-.]([A-Z0-9_]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return (prefix, tag.Success ? tag.Groups[1].Value.ToUpperInvariant() : "", index, count);
    }

    public static IReadOnlyList<GgufVariant> ParseGgufVariants(JsonElement metadata) =>
        ParseGgufRepository(metadata).Variants;

    public static GgufRepository ParseGgufRepository(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("siblings", out var siblings) ||
            siblings.ValueKind != JsonValueKind.Array) return new(true, false, [], []);
        var files = new List<(string Path, long? Size)>();
        foreach (var item in siblings.EnumerateArray().Take(5000))
        {
            if (!item.TryGetProperty("rfilename", out var path) || path.ValueKind != JsonValueKind.String) continue;
            long? size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : null;
            if (item.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object &&
                lfs.TryGetProperty("size", out var lfsSize) && lfsSize.TryGetInt64(out var lfsBytes)) size = lfsBytes;
            files.Add((path.GetString() ?? "", size));
        }
        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        var models = files.Where(file => IsGgufModelFile(file.Path)).ToArray();
        var result = new Dictionary<string, GgufVariant>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in models)
        {
            var split = GgufSplit(file.Path);
            if (split.Index != 1 || split.Tag.Length == 0) continue;
            var parts = models.Where(other => GgufSplit(other.Path) is var info &&
                info.Prefix == split.Prefix && info.Count == split.Count).ToArray();
            long? size = parts.All(part => part.Size is not null) ? parts.Sum(part => part.Size!.Value) : null;
            result.TryAdd(split.Tag, new GgufVariant(split.Tag, file.Path, split.Count, size));
        }
        var variants = result.Values.OrderBy(x => x.Quantization == "Q4_K_M" ? 0 : 1)
            .ThenBy(x => x.Quantization, StringComparer.Ordinal).Take(40).ToArray();
        return new(true, false, models.Select(file => file.Path).ToArray(), variants);
    }

    /// <summary>Mirror llama.cpp's repo[:quant] resolution so impossible installs fail before queueing.</summary>
    public static string? ExplainUnresolvableGguf(GgufRepository repository, string repoId, string? tag)
    {
        if (!repository.Found)
            return $"Hugging Face repository '{repoId}' was not found. Check the org/model ID.";
        if (repository.Gated) return null;
        if (repository.ModelFiles.Count == 0)
            return $"'{repoId}' contains no GGUF model files, so llama.cpp cannot load it. " +
                   "Choose a GGUF repository (for example one ending in -GGUF) or install with vLLM/SGLang.";
        if (string.IsNullOrEmpty(tag)) return null;
        var pattern = new Regex("(^|[-./])" + Regex.Escape(tag) + "[.-]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (repository.ModelFiles.Any(file => pattern.IsMatch(file) && GgufSplit(file).Index == 1)) return null;
        return $"No GGUF file in '{repoId}' matches quantization '{tag}'." +
               (repository.Variants.Count > 0
                   ? " Available: " + string.Join(", ", repository.Variants.Select(x => x.Quantization)) + "."
                   : "");
    }

    public async Task<IReadOnlyList<GgufVariant>> GgufVariantsAsync(string repoId, CancellationToken token)
    {
        var repository = await GgufRepositoryAsync(repoId, token);
        return repository.Found && !repository.Gated ? repository.Variants : [];
    }

    public async Task<GgufRepository> GgufRepositoryAsync(string repoId, CancellationToken token)
    {
        if (!ValidRepository(repoId)) throw new ArgumentException("Invalid Hugging Face repository ID.");
        var key = "huggingface:gguf-repository:" + repoId;
        return await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            using var response = await clients.CreateClient("huggingface-hub")
                .GetAsync("https://huggingface.co/api/models/" + repoId + "?blobs=true", token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new GgufRepository(false, false, [], []);
            // Private or gated metadata: the Agent may hold HF_TOKEN, so do not block here.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new GgufRepository(true, true, [], []);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var model = Parse(document.RootElement);
            var parsed = ParseGgufRepository(document.RootElement);
            return model is null || model.Private || model.Gated ? parsed with { Gated = true } : parsed;
        }) ?? new GgufRepository(true, true, [], []);
    }

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
