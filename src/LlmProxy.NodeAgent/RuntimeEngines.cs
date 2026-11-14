using System.Globalization;
using System.Text.Json;

namespace LlmProxy.NodeAgent;

/// <summary>A serving engine the Agent can run models with: a pulled public image or an adapter the Agent builds itself.</summary>
public sealed record RuntimeEngineDefinition(string Id, string Label, string Description, bool Built, string[] Serves, string? BuildFolder = null);

public sealed record RuntimeEngineState(
    string Id, string Label, string Description,
    /// <summary><c>pulled</c> from a registry or <c>built</c> by this Agent from its bundled adapter.</summary>
    string Source,
    string Image, bool Installed, string? Version, string? Revision, string? ImageId, DateTimeOffset? CreatedAtUtc, long? SizeBytes,
    int RunningContainers, int Installations, string[] Serves);

public sealed record RuntimeEngineUpdateCheck(string Id, string Status, string? Detail);

public sealed record RuntimeEngineResult(bool Changed, string? PreviousImageId, string? ImageId, string Message);

public static class RuntimeEngineCatalog
{
    public static IReadOnlyList<RuntimeEngineDefinition> All { get; } =
    [
        new("vllm", "vLLM", "High-throughput GPU server for chat models (OpenAI-compatible).", false, ["OpenAi"]),
        new("llama.cpp", "llama.cpp", "GGUF models on GPU or CPU; also serves embeddings.", false, ["OpenAi", "Embedding"]),
        new("sglang", "SGLang", "Alternative GPU server for chat models.", false, ["OpenAi"]),
        new("airllm", "AirLLM", "Layer-by-layer runner for models larger than GPU memory (slow, experimental).", true, ["OpenAi"], "airllm-runtime"),
        new("faster-whisper", "faster-whisper", "Speech to text (Whisper) on CPU.", true, ["AudioTranscription"], "faster-whisper-runtime"),
        new("kokoro", "Kokoro", "Text to speech (experimental).", true, ["SpeechSynthesis"], "kokoro-runtime"),
        new("diffusers", "Diffusers", "Image generation (experimental).", true, ["ImageGeneration"], "diffusers-runtime"),
        new("laya", "Laya", "System One classifier on CPU (experimental).", true, ["SystemOne"], "laya-runtime")
    ];

    public static RuntimeEngineDefinition? Find(string? id) =>
        All.FirstOrDefault(engine => string.Equals(engine.Id, id, StringComparison.Ordinal));

    public static string Image(RuntimeEngineDefinition engine, NodeAgentOptions options) =>
        ManagedRuntimeProfiles.ImageForRuntime(engine.Id, options);

    /// <summary>AirLLM and Diffusers have an ARM64 recipe; the other adapters build the same Dockerfile everywhere.</summary>
    public static string DockerfileName(RuntimeEngineDefinition engine, bool arm64) =>
        arm64 && engine.Id is "airllm" or "diffusers" ? "Dockerfile.arm64" : "Dockerfile";
}

/// <summary>What <c>docker image inspect</c> says about an image, reduced to what the inventory shows.</summary>
public sealed record InspectedImage(string Id, DateTimeOffset? CreatedAtUtc, long? SizeBytes,
    IReadOnlyDictionary<string, string> Labels, IReadOnlyList<string> RepoDigests);

public static class RuntimeEngineParsing
{
    public static InspectedImage? ParseInspect(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                ? document.RootElement[0] : document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Id", out var id) || id.GetString() is not { Length: > 0 } imageId)
                return null;
            DateTimeOffset? created = root.TryGetProperty("Created", out var createdValue) && createdValue.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(createdValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : null;
            long? size = root.TryGetProperty("Size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : null;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object &&
                config.TryGetProperty("Labels", out var labelObject) && labelObject.ValueKind == JsonValueKind.Object)
                foreach (var label in labelObject.EnumerateObject())
                    if (label.Value.ValueKind == JsonValueKind.String) labels[label.Name] = label.Value.GetString()!;
            var digests = root.TryGetProperty("RepoDigests", out var digestArray) && digestArray.ValueKind == JsonValueKind.Array
                ? digestArray.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToArray() : [];
            return new InspectedImage(imageId, created, size, labels, digests);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The tag of an image reference, or null when there is none (a registry port is not a tag).</summary>
    public static string? TagOf(string image)
    {
        var withoutDigest = image.Split('@')[0];
        var colon = withoutDigest.LastIndexOf(':');
        return colon > withoutDigest.LastIndexOf('/') && colon >= 0 ? withoutDigest[(colon + 1)..] : null;
    }

    /// <summary>The release the image claims in its OCI labels, otherwise the tag it was pulled or built under.</summary>
    public static string? VersionOf(string image, InspectedImage inspected) =>
        inspected.Labels.GetValueOrDefault("org.opencontainers.image.version") is { Length: > 0 } label ? label : TagOf(image);

    public static string? RevisionOf(InspectedImage inspected) =>
        inspected.Labels.GetValueOrDefault("org.opencontainers.image.revision") is { Length: > 0 } revision
            ? (revision.Length > 12 ? revision[..12] : revision) : null;

    /// <summary>True when the registry's digest is not among the digests this image was pulled with.</summary>
    public static bool IsBehindRegistry(IReadOnlyList<string> localRepoDigests, string remoteDigest) =>
        localRepoDigests.Count > 0 && !localRepoDigests.Any(entry => entry.EndsWith("@" + remoteDigest, StringComparison.Ordinal));
}
