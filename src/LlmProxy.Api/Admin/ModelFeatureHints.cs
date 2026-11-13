using System.Text.Json;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Reads what a Hugging Face repository itself declares about a model: tool calling and reasoning from its chat
/// template, vision from the pipeline tag and config, context window from the config. These are declarations
/// by the publisher, not a test; the operator can change every value and a live check can confirm it.
/// </summary>
public static class ModelFeatureHints
{
    public sealed record Detected(bool Tools, bool Vision, bool Reasoning, int? ContextWindow, IReadOnlyList<string> Evidence)
    {
        public static readonly Detected None = new(false, false, false, null, []);
    }

    private static readonly string[] ToolMarkers =
        ["tool_calls", "tool_call", "tools", "function_call", "<|python_tag|>", "[TOOL_CALLS]", "tool_response", "ipython"];

    private static readonly string[] ReasoningMarkers =
        ["<think>", "</think>", "enable_thinking", "reasoning_content", "reasoning effort", "reasoning_effort", "reasoning strength", "thinking_budget", "to=self"];

    private static readonly string[] VisionTags = ["image-text-to-text", "visual-question-answering", "image-to-text", "vision", "multimodal"];

    public static Detected FromHub(IReadOnlyCollection<string> tags, string? pipelineTag, string? chatTemplate,
        bool hasVisionConfig, bool hasMmprojFile, int? contextWindow)
    {
        var evidence = new List<string>();
        var template = chatTemplate ?? "";

        var tools = template.Length > 0 && ToolMarkers.Any(marker => template.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (tools) evidence.Add("The chat template renders tool definitions and tool calls.");

        var reasoning = (template.Length > 0 && ReasoningMarkers.Any(marker => template.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            || tags.Contains("reasoning", StringComparer.OrdinalIgnoreCase);
        if (reasoning) evidence.Add("The chat template has a reasoning channel (thinking or reasoning effort).");

        var vision = hasVisionConfig || hasMmprojFile
            || (pipelineTag is not null && VisionTags.Contains(pipelineTag, StringComparer.OrdinalIgnoreCase))
            || tags.Any(tag => VisionTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        if (vision)
            evidence.Add(hasMmprojFile ? "The repository ships a multimodal projector (mmproj) file."
                : pipelineTag is not null && VisionTags.Contains(pipelineTag, StringComparer.OrdinalIgnoreCase)
                    ? $"The repository's pipeline is {pipelineTag}."
                    : "The configuration includes a vision encoder.");

        if (contextWindow is { } window) evidence.Add($"The configuration declares a context of {window:N0} tokens.");
        return new Detected(tools, vision, reasoning, contextWindow, evidence);
    }

    /// <summary>The chat template as published in the Hub API summary (safetensors repos) or GGUF metadata.</summary>
    public static string? ChatTemplate(JsonElement model)
    {
        static string? read(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Array => string.Join("\n", element.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()
                    : item.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                .Where(text => text is not null)),
            _ => null
        };

        if (model.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object &&
            gguf.TryGetProperty("chat_template", out var ggufTemplate) && read(ggufTemplate) is { Length: > 0 } fromGguf)
            return fromGguf;

        if (model.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
        {
            if (config.TryGetProperty("chat_template_jinja", out var jinja) && read(jinja) is { Length: > 0 } direct) return direct;
            if (config.TryGetProperty("tokenizer_config", out var tokenizer) && tokenizer.ValueKind == JsonValueKind.Object &&
                tokenizer.TryGetProperty("chat_template", out var inner) && read(inner) is { Length: > 0 } nested) return nested;
        }
        return null;
    }

    public static bool HasVisionConfig(JsonElement? configJson)
    {
        if (configJson is not { ValueKind: JsonValueKind.Object } config) return false;
        return config.TryGetProperty("vision_config", out _) || config.TryGetProperty("visual", out _) ||
               config.TryGetProperty("image_token_id", out _) || config.TryGetProperty("vision_tower", out _);
    }
}
