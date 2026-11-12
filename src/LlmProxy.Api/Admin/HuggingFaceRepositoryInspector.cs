using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Reads public Hugging Face Hub metadata for one repository (parameters, weight formats, context length,
/// GGUF quantizations) and lists community quantized builds, so the Admin can pre-fill a custom install.
/// Repository IDs are validated; only fixed Hub hosts and bounded responses are used.
/// </summary>
public sealed class HuggingFaceRepositoryInspector(IHttpClientFactory clients, IMemoryCache cache)
{
    private const int MaxConfigBytes = 512 * 1024;
    private const int SuggestedContextCap = 32768;

    public sealed record EngineSuggestion(string Runtime, string Reason, int ContextTokens);

    public sealed record RepositoryInspection(
        string Id, bool Found, bool Gated, bool Private, string? License, string? PipelineTag, string? LibraryName,
        string? Architecture, long? ParameterCount, string? Precision, long? WeightsBytes, int? ContextLength,
        string? Quantization, bool Multimodal, bool HasGguf, bool HasSafetensors,
        IReadOnlyList<HuggingFaceDiscoveryService.GgufVariant> GgufVariants, EngineSuggestion? Suggestion, string Url);

    public sealed record QuantizedDerivative(
        string Id, string Format, long Downloads, long Likes, long? ParameterCount, DateTimeOffset? LastModified);

    private static readonly string[] MultimodalPipelines =
        ["image-text-to-text", "video-text-to-text", "audio-text-to-text", "any-to-any", "visual-question-answering"];

    // Most specific tag first: a GPTQ repo is also tagged "4-bit".
    private static readonly (string Tag, string Format)[] FormatTags =
    [
        ("gguf", "gguf"), ("awq", "awq"), ("gptq", "gptq"), ("fp8", "fp8"), ("compressed-tensors", "compressed-tensors"),
        ("bitsandbytes", "bitsandbytes"), ("exl2", "exl2"), ("mlx", "mlx"), ("4-bit", "4-bit"), ("8-bit", "8-bit")
    ];

    public static string? FormatFromTags(IEnumerable<string> tags)
    {
        var set = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, format) in FormatTags)
            if (set.Contains(tag)) return format;
        return null;
    }

    public static EngineSuggestion? Suggest(bool hasGguf, int ggufVariants, bool hasSafetensors,
        string? quantization, int? contextLength)
    {
        var context = Math.Clamp(contextLength ?? 8192, 256, SuggestedContextCap);
        if (hasSafetensors)
        {
            var reason = quantization is "awq" or "gptq" or "fp8" or "compressed-tensors" or "bitsandbytes"
                ? $"Pre-quantized ({quantization.ToUpperInvariant()}) safetensors weights; vLLM serves these directly."
                : "Safetensors weights; vLLM serves them directly. Smaller GGUF or quantized builds from other publishers are listed below when they exist.";
            return new("vllm", reason, context);
        }
        if (hasGguf || ggufVariants > 0)
            return new("llama.cpp", "This repository publishes GGUF files; llama.cpp serves them directly. Pick a quantization below.", context);
        return null;
    }

    public static RepositoryInspection ParseInspection(string id, JsonElement model, JsonElement? configJson)
    {
        string? str(JsonElement obj, string property) => obj.TryGetProperty(property, out var v) &&
            v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var tags = model.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
            ? tagArray.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
        var gated = model.TryGetProperty("gated", out var gatedValue) &&
            (gatedValue.ValueKind == JsonValueKind.True ||
             gatedValue.ValueKind == JsonValueKind.String && gatedValue.GetString() is not ("false" or "" or null));
        var isPrivate = model.TryGetProperty("private", out var privateValue) && privateValue.ValueKind == JsonValueKind.True;
        var license = tags.FirstOrDefault(tag => tag.StartsWith("license:", StringComparison.Ordinal))?["license:".Length..];
        var pipeline = str(model, "pipeline_tag");

        string? architecture = null;
        if (model.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
        {
            if (config.TryGetProperty("architectures", out var architectures) && architectures.ValueKind == JsonValueKind.Array)
                architecture = architectures.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.String
                    ? architectures.EnumerateArray().First().GetString() : null;
            architecture ??= str(config, "model_type");
        }

        long? parameters = null;
        string? precision = null;
        if (model.TryGetProperty("safetensors", out var safetensors) && safetensors.ValueKind == JsonValueKind.Object)
        {
            if (safetensors.TryGetProperty("total", out var total) && total.TryGetInt64(out var count)) parameters = count;
            if (safetensors.TryGetProperty("parameters", out var byType) && byType.ValueKind == JsonValueKind.Object)
                precision = string.Join(" + ", byType.EnumerateObject()
                    .Where(item => item.Value.ValueKind == JsonValueKind.Number)
                    .OrderByDescending(item => item.Value.GetInt64()).Take(2).Select(item => item.Name));
            if (precision is { Length: 0 }) precision = null;
        }

        int? context = null;
        if (model.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object)
        {
            if (parameters is null && gguf.TryGetProperty("total", out var ggufTotal) && ggufTotal.TryGetInt64(out var ggufCount))
                parameters = ggufCount;
            if (gguf.TryGetProperty("context_length", out var ggufContext) && ggufContext.TryGetInt32(out var ggufTokens))
                context = ggufTokens;
            architecture ??= str(gguf, "architecture");
        }

        string? quantization = FormatFromTags(tags);
        if (configJson is { ValueKind: JsonValueKind.Object } raw)
        {
            context ??= ReadContext(raw);
            if (raw.TryGetProperty("quantization_config", out var quant) && quant.ValueKind == JsonValueKind.Object &&
                str(quant, "quant_method") is { Length: > 0 } method && quantization is null or "4-bit" or "8-bit")
                quantization = method.ToLowerInvariant();
        }

        var files = HuggingFaceDiscoveryService.ParseGgufRepository(model);
        var weightFiles = SiblingFiles(model).ToArray();
        var safetensorFiles = weightFiles.Where(file => file.Path.EndsWith(".safetensors", StringComparison.Ordinal)).ToArray();
        var hasSafetensors = safetensorFiles.Length > 0 || tags.Contains("safetensors", StringComparer.OrdinalIgnoreCase);
        long? weights = safetensorFiles.Length > 0 && safetensorFiles.All(file => file.Size is not null)
            ? safetensorFiles.Sum(file => file.Size!.Value) : null;
        var hasGguf = files.Variants.Count > 0 || tags.Contains("gguf", StringComparer.OrdinalIgnoreCase);

        var inspection = new RepositoryInspection(
            id, true, gated || isPrivate, isPrivate, license, pipeline, str(model, "library_name"),
            architecture, parameters, precision, weights, context, quantization,
            pipeline is not null && MultimodalPipelines.Contains(pipeline), hasGguf, hasSafetensors,
            files.Variants, null, "https://huggingface.co/" + id);
        return inspection with { Suggestion = Suggest(hasGguf, files.Variants.Count, hasSafetensors, quantization, context) };
    }

    private static IEnumerable<(string Path, long? Size)> SiblingFiles(JsonElement model)
    {
        if (!model.TryGetProperty("siblings", out var siblings) || siblings.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in siblings.EnumerateArray().Take(5000))
        {
            if (!item.TryGetProperty("rfilename", out var path) || path.ValueKind != JsonValueKind.String) continue;
            long? size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : null;
            if (item.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object &&
                lfs.TryGetProperty("size", out var lfsSize) && lfsSize.TryGetInt64(out var lfsBytes)) size = lfsBytes;
            yield return (path.GetString() ?? "", size);
        }
    }

    // Multimodal checkpoints keep the language-model settings under text_config.
    public static int? ReadContext(JsonElement config)
    {
        foreach (var scope in new[] { config, config.TryGetProperty("text_config", out var text) ? text : default })
        {
            if (scope.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in new[] { "max_position_embeddings", "max_sequence_length", "seq_length", "n_positions" })
                if (scope.TryGetProperty(key, out var value) && value.TryGetInt32(out var tokens) && tokens is >= 256 and <= 10_000_000)
                    return tokens;
        }
        return null;
    }

    public static IReadOnlyList<QuantizedDerivative> ParseDerivatives(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<QuantizedDerivative>();
        foreach (var item in list.EnumerateArray().Take(60))
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idValue) ||
                idValue.ValueKind != JsonValueKind.String || idValue.GetString() is not { } id ||
                !HuggingFaceDiscoveryService.ValidRepository(id)) continue;
            var tags = item.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
                ? tagArray.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            var format = FormatFromTags(tags);
            if (format is null) continue;
            long number(string property) => item.TryGetProperty(property, out var v) && v.TryGetInt64(out var n) ? n : 0;
            long? parameters = item.TryGetProperty("safetensors", out var st) && st.ValueKind == JsonValueKind.Object &&
                st.TryGetProperty("total", out var total) && total.TryGetInt64(out var count) ? count
                : item.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object &&
                  gguf.TryGetProperty("total", out var ggufTotal) && ggufTotal.TryGetInt64(out var ggufCount) ? ggufCount : null;
            DateTimeOffset? modified = item.TryGetProperty("lastModified", out var last) && last.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(last.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : null;
            result.Add(new(id, format, number("downloads"), number("likes"), parameters, modified));
        }
        return result;
    }

    public async Task<RepositoryInspection> InspectAsync(string repoId, CancellationToken token)
    {
        if (!HuggingFaceDiscoveryService.ValidRepository(repoId)) throw new ArgumentException("Invalid Hugging Face repository ID.");
        return (await cache.GetOrCreateAsync("huggingface:inspect:" + repoId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var client = clients.CreateClient("huggingface-hub");
            using var response = await client.GetAsync("https://huggingface.co/api/models/" + repoId + "?blobs=true", token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Missing(repoId, gated: false);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return Missing(repoId, gated: true) with { Found = true };
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);

            // config.json holds the context window and quantization config that the API summary omits.
            using var configDocument = await TryReadConfigAsync(client, repoId, token);
            return ParseInspection(repoId, document.RootElement, configDocument?.RootElement);
        }))!;
    }

    public async Task<IReadOnlyList<QuantizedDerivative>> QuantizedBuildsAsync(string repoId, CancellationToken token)
    {
        if (!HuggingFaceDiscoveryService.ValidRepository(repoId)) throw new ArgumentException("Invalid Hugging Face repository ID.");
        return (await cache.GetOrCreateAsync("huggingface:quantized:" + repoId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var uri = "https://huggingface.co/api/models?filter=" + Uri.EscapeDataString("base_model:quantized:" + repoId) +
                "&sort=downloads&direction=-1&limit=30&expand[]=downloads&expand[]=likes&expand[]=tags" +
                "&expand[]=gguf&expand[]=safetensors&expand[]=lastModified";
            using var response = await clients.CreateClient("huggingface-hub").GetAsync(uri, token);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            return ParseDerivatives(document.RootElement);
        }))!;
    }

    private static async Task<JsonDocument?> TryReadConfigAsync(HttpClient client, string repoId, CancellationToken token)
    {
        try
        {
            using var response = await client.GetAsync(
                "https://huggingface.co/" + repoId + "/resolve/main/config.json", HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxConfigBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, token);
            if (buffer.Length > MaxConfigBytes) return null;
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: token);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException
            && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    private static RepositoryInspection Missing(string id, bool gated) => new(
        id, false, gated, false, null, null, null, null, null, null, null, null, null, false, false, false, [], null,
        "https://huggingface.co/" + id);
}
