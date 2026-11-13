using System.Text.Json;
using System.Text.Json.Serialization;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;

namespace LlmProxy.Api.OpenAi;

/// <summary>Public description of a model for <c>GET /v1/models/details</c>.</summary>
public static class ModelDetails
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static object From(PublicModel model)
    {
        var chat = model.Surface == ModelSurface.OpenAi;
        var input = model.Surface switch
        {
            ModelSurface.AudioTranscription => new[] { "audio" },
            ModelSurface.OpenAi when model.SupportsVision => new[] { "text", "image" },
            _ => new[] { "text" }
        };
        var output = model.Surface switch
        {
            ModelSurface.ImageGeneration => new[] { "image" },
            ModelSurface.SpeechSynthesis => new[] { "audio" },
            _ => new[] { "text" }
        };
        var endpoints = model.Surface switch
        {
            ModelSurface.OpenAi => new[] { "/v1/chat/completions", "/v1/responses" },
            ModelSurface.ImageGeneration => new[] { "/v1/images/generations" },
            ModelSurface.SpeechSynthesis => new[] { "/v1/audio/speech" },
            ModelSurface.AudioTranscription => new[] { "/v1/audio/transcriptions", "/v1/audio/translations" },
            _ => Array.Empty<string>()
        };

        return new
        {
            id = model.PublicName,
            @object = "model",
            created = 0,
            owned_by = "llmproxy",
            llmproxy_surface = model.Surface.ToString(),
            // Prompt plus completion the model accepts, and the largest completion; null when the operator has not declared them.
            context_window = chat ? model.ContextWindowTokens : null,
            max_output_tokens = chat ? model.MaxOutputTokens : null,
            input_modalities = input,
            output_modalities = output,
            endpoints,
            capabilities = new
            {
                streaming = chat && model.SupportsStreaming,
                tools = chat && model.SupportsTools,
                vision = chat && model.SupportsVision,
                reasoning = chat && model.SupportsReasoning
            }
        };
    }
}
