namespace LlmProxy.Api.Admin;

/// <summary>
/// Conservative, inspectable capability declarations. These are NOT benchmarks
/// and do not imply that a given host has passed a live Chat/Tools/SSE test.
/// Do not infer model features from the runtime alone or vice versa.
/// </summary>
public static class ModelCapabilitiesCatalog
{
    public sealed record Feature(string Model, string Serving, string Explanation);
    public sealed record Profile(
        string ProviderModelName,
        string Runtime,
        string Source,
        string? ModelCardUrl,
        Feature Chat,
        Feature ToolCalling,
        Feature Streaming,
        Feature Vision,
        Feature Thinking,
        int? NativeContextTokens,
        int? ExtendedContextTokens,
        int? ServingContextTokens,
        int? MaximumOutputTokens,
        string Notes,
        Feature? AudioTranscription = null);

    public static Profile Resolve(string providerModelName, string runtime, int? maxModelLen = null, int? maxOutputTokens = null)
    {
        var name = providerModelName?.Trim() ?? "";
        var serving = runtime?.Trim().ToLowerInvariant() ?? "";
        var qwen3 = name is
            "Qwen/Qwen3-4B" or "Qwen/Qwen3-8B" or "Qwen/Qwen3-14B" or "Qwen/Qwen3-32B"
            or "Qwen/Qwen3-4B-AWQ" or "Qwen/Qwen3-8B-AWQ"
            or "ggml-org/Qwen3-4B-GGUF:Q4_K_M";
        var standardQwen = name is
            "Qwen/Qwen3-4B" or "Qwen/Qwen3-8B" or "Qwen/Qwen3-14B" or "Qwen/Qwen3-32B";
        var mistralText = name == "mistralai/Mistral-7B-Instruct-v0.3";
        var knownText = qwen3 || mistralText;
        var modelCardUrl = name.Contains(':')
            ? "https://huggingface.co/" + name.Split(':')[0]
            : name.Contains('/') && name.Length <= 220
                ? "https://huggingface.co/" + name
                : null;

        if (serving == "laya")
        {
            var unsupported = new Feature("unsupported", "unsupported",
                "Laya implements typed System One classification, not OpenAI Chat, vision, audio or image generation.");
            return new Profile(name, serving, "Laya typed classifier (CPU English experimental profile)",
                modelCardUrl, unsupported, unsupported, unsupported, unsupported, unsupported,
                null, null, null, null,
                "POST /v1/systemone accepts state and typed questions, returning answers and probabilities. " +
                "Managed checkpoint is pinned and startup requires an actual classifier response. " +
                "No Chat token context or GPU acceleration is implied.");
        }
        if (serving == "faster-whisper")
        {
            var unsupported = new Feature("unsupported", "unsupported",
                "This checkpoint and runtime handle audio input only, not Chat, vision, tools, image generation or TTS.");
            return new Profile(name, serving, "Systran faster-whisper CTranslate2 checkpoints + local CPU adapter",
                modelCardUrl, unsupported, unsupported, unsupported, unsupported, unsupported,
                null, null, null, null,
                "Speech transcription and English translation only. Audio upload limit 25 MiB. " +
                "CPU int8 on x64/ARM64; no promised GPU speedup or Chat token context. " +
                "Verify actual audio latency and transcript correctness before routing traffic.",
                new Feature("supported", "supported",
                    "OpenAI-compatible POST /v1/audio/transcriptions and /v1/audio/translations, " +
                    "file upload with json, verbose_json, text, srt or vtt output."));
        }

        // Runtime properties are adapter/API facts; model properties are separate.
        // Unknown checkpoints never inherit Qwen3 claims from similar-looking names.
        var chat = new Feature(knownText ? "supported" : "unknown",
            serving is "vllm" or "sglang" or "llama.cpp" or "airllm" ? "supported" : "unknown",
            "OpenAI-compatible text Chat endpoint; a successful generation is still required for runtime acceptance.");

        Feature tools = qwen3
            ? new("supported", serving == "airllm" ? "unsupported" : "unverified",
                serving == "airllm"
                    ? "Qwen3 supports function calling, but this AirLLM adapter does not accept or parse tools."
                    : "The model supports tool use; the managed launch has not verified its tool-call parser and structured API responses.")
            : new("unknown", serving == "airllm" ? "unsupported" : "unverified",
                "Tool calling has not been established for this checkpoint and serving configuration.");

        Feature streaming = new("not-applicable",
            serving == "airllm" ? "unsupported" :
                serving is "vllm" or "sglang" or "llama.cpp" ? "supported" : "unknown",
            serving == "airllm"
                ? "This AirLLM API explicitly rejects stream=true; loading layers is not token streaming."
                : "SSE is a serving-API feature; verify the actual endpoint before advertising it in production.");

        Feature vision = new(knownText ? "unsupported" : "unknown",
            serving == "airllm" || knownText ? "unsupported" : "unverified",
            knownText
                ? "This is a text-only checkpoint, not a vision-language variant."
                : "Vision needs a verified multimodal checkpoint, processor, and compatible runtime.");

        Feature thinking = qwen3
            ? new("supported", "partial",
                serving == "airllm"
                    ? "Qwen3 can emit <think> text using its chat template, but AirLLM exposes no per-request thinking switch or structured reasoning_content."
                    : "Qwen3 supports thinking and non-thinking modes, but a managed runtime needs verified thinking controls and a reasoning parser for structured client integration.")
            : new("unknown", "unverified",
                "Dedicated reasoning mode has not been established for this checkpoint and adapter.");

        int? nativeContext = qwen3 ? 32768 : mistralText ? 32768 : null;
        int? extendedContext = standardQwen ? 131072 : null;
        // The embedded AirLLM adapter enforces MAX_CONTEXT at tokenization and
        // caps max_tokens to the configured per-installation limit (default 256).
        int? activeContext = serving == "airllm" ? maxModelLen ?? 8192 : maxModelLen;
        int? maxOutput = serving == "airllm" ? maxOutputTokens ?? 256 : null;
        var notes = serving == "airllm"
            ? "Experimental single-generator AirLLM. Max input context includes prompt/template tokens. Health 200 is not a verified tools/SSE/multimodal response."
            : "Capabilities are catalog/documentation declarations, not a successful feature probe. Unverified entries require a real serving test.";

        return new Profile(name, serving,
            qwen3 ? "Qwen3 published model card + local adapter contract" :
                mistralText ? "Curated text model + local adapter contract" :
                "Unknown checkpoint; no trusted feature declaration",
            modelCardUrl, chat, tools, streaming, vision, thinking,
            nativeContext, extendedContext, activeContext, maxOutput, notes);
    }
}
