namespace LlmProxy.Api.Admin;

/// <summary>
/// The properties a model is published with: what clients may expect (tools, vision, reasoning, streaming) and its
/// limits. They are suggested from the curated catalog and from what the Hugging Face repository declares, then
/// the operator reviews and can change every one before or after install.
/// </summary>
public static class ModelPropertyResolver
{
    public sealed record ModelProperties(
        bool Streaming, bool Tools, bool Vision, bool Reasoning, int? ContextWindow, int? MaxOutputTokens);

    /// <summary>What the operator may override at install; a null field keeps the suggested value.</summary>
    public sealed record DeclaredProperties(
        bool? Streaming = null, bool? Tools = null, bool? Vision = null, bool? Reasoning = null,
        int? ContextWindow = null, int? MaxOutputTokens = null,
        string? Surface = null);

    public sealed record Suggestion(ModelProperties Properties, string Source, IReadOnlyList<string> Evidence);

    private static readonly string[] ChatRuntimes = ["vllm", "sglang", "llama.cpp", "airllm"];

    /// <param name="maxModelLen">Context the runtime is started with (the pool shared by all slots for llama.cpp).</param>
    /// <param name="maxNumSeqs">Parallel sequences; llama.cpp divides its context among them.</param>
    public static Suggestion Suggest(
        string providerModelName, string runtime, ModelFeatureHints.Detected? detected,
        int? maxModelLen, int? maxNumSeqs, int? maxOutputTokens)
    {
        var profile = ModelCapabilitiesCatalog.Resolve(providerModelName, runtime, maxModelLen, maxOutputTokens);
        var hub = detected ?? ModelFeatureHints.Detected.None;
        var chat = ChatRuntimes.Contains(runtime, StringComparer.Ordinal);
        var evidence = new List<string>();

        var streaming = chat && runtime != "airllm";
        if (runtime == "airllm") evidence.Add("The AirLLM runtime cannot stream tokens.");

        var curatedTools = profile.ToolCalling.Model == "supported";
        var tools = chat && runtime != "airllm" && (curatedTools || hub.Tools);
        if (curatedTools) evidence.Add("Listed in the curated catalog with tool calling.");
        else if (hub.Tools && runtime != "airllm") evidence.Add("Hugging Face chat template declares tool calls.");
        if (runtime == "airllm") evidence.Add("The AirLLM adapter does not parse tool calls.");

        var vision = chat && runtime != "airllm" && profile.Vision.Model != "unsupported" &&
            (profile.Vision.Model == "supported" || hub.Vision);
        if (vision) evidence.Add("The repository declares image input (pipeline, config or projector file).");

        var reasoning = chat && (profile.Thinking.Model == "supported" || hub.Reasoning);
        if (reasoning) evidence.Add("The model has a reasoning mode (curated catalog or chat template).");

        int? servedContext = maxModelLen;
        if (servedContext is { } pool && runtime == "llama.cpp" && maxNumSeqs is > 1)
            servedContext = Math.Max(256, pool / maxNumSeqs.Value);
        servedContext ??= hub.ContextWindow ?? profile.NativeContextTokens;

        int? output = runtime == "airllm" ? maxOutputTokens ?? profile.MaximumOutputTokens
            : servedContext is { } window ? Math.Clamp(window / 4, 256, 8192) : null;
        if (output is { } cap && servedContext is { } limit && cap > limit) output = limit;

        var source = profile.Source.StartsWith("Unknown", StringComparison.Ordinal)
            ? hub.Evidence.Count > 0 ? "Hugging Face repository metadata" : "No published information; review each value"
            : profile.Source;
        evidence.AddRange(hub.Evidence.Where(item => !evidence.Contains(item)));
        return new Suggestion(new ModelProperties(streaming, tools, vision, reasoning, servedContext, output), source, evidence);
    }

    /// <summary>Declared values win; everything else keeps the suggestion.</summary>
    public static ModelProperties Apply(ModelProperties suggested, DeclaredProperties? declared) => declared is null ? suggested : new(
        declared.Streaming ?? suggested.Streaming, declared.Tools ?? suggested.Tools,
        declared.Vision ?? suggested.Vision, declared.Reasoning ?? suggested.Reasoning,
        declared.ContextWindow ?? suggested.ContextWindow, declared.MaxOutputTokens ?? suggested.MaxOutputTokens);
}
