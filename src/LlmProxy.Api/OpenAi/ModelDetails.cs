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

    /// <summary>The kind of model as clients filter it: <c>chat</c>, <c>embedding</c>, <c>image</c>, <c>speech</c>, <c>transcription</c>, <c>realtime</c>.</summary>
    public static string TypeOf(ModelSurface surface) => surface switch
    {
        ModelSurface.OpenAi => "chat",
        ModelSurface.Embedding => "embedding",
        ModelSurface.ImageGeneration => "image",
        ModelSurface.SpeechSynthesis => "speech",
        ModelSurface.AudioTranscription => "transcription",
        ModelSurface.Realtime => "realtime",
        _ => "classifier"
    };

    public static object From(PublicModel model)
    {
        var chat = model.Surface == ModelSurface.OpenAi;
        var input = model.Surface switch
        {
            ModelSurface.AudioTranscription => new[] { "audio" },
            ModelSurface.Realtime => new[] { "audio", "text" },
            ModelSurface.OpenAi when model.SupportsVision => new[] { "text", "image" },
            _ => new[] { "text" }
        };
        var output = model.Surface switch
        {
            ModelSurface.ImageGeneration => new[] { "image" },
            ModelSurface.SpeechSynthesis => new[] { "audio" },
            ModelSurface.Embedding => new[] { "embedding" },
            ModelSurface.Realtime => new[] { "audio", "text" },
            _ => new[] { "text" }
        };
        var endpoints = model.Surface switch
        {
            ModelSurface.OpenAi => new[] { "/v1/chat/completions", "/v1/responses" },
            ModelSurface.ImageGeneration => new[] { "/v1/images/generations" },
            ModelSurface.SpeechSynthesis => new[] { "/v1/audio/speech" },
            ModelSurface.AudioTranscription => new[] { "/v1/audio/transcriptions", "/v1/audio/translations" },
            ModelSurface.Embedding => new[] { "/v1/embeddings" },
            ModelSurface.Realtime => new[] { "/v1/realtime" },
            _ => new[] { "/v1/systemone" }
        };

        return new
        {
            id = model.PublicName,
            @object = "model",
            created = 0,
            owned_by = "llmproxy",
            type = TypeOf(model.Surface),
            llmproxy_surface = model.Surface.ToString(),
            // Prompt plus completion the model accepts, and the largest completion; null when the operator has not declared them.
            context_window = chat || model.Surface == ModelSurface.Embedding ? model.ContextWindowTokens : null,
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

    private static readonly ModelSurface[] Order =
    [
        ModelSurface.OpenAi, ModelSurface.ImageGeneration, ModelSurface.SpeechSynthesis, ModelSurface.AudioTranscription,
        ModelSurface.Embedding, ModelSurface.Realtime, ModelSurface.SystemOne
    ];

    /// <summary>
    /// Every way a client can use the gateway and the models that answer it right now, for <c>GET /v1/modalities</c>.
    /// A modality with no running model is still listed (with an empty <c>models</c>), so a client can tell "not offered"
    /// from "offered but nothing is running".
    /// </summary>
    public static object Modalities(IEnumerable<PublicModel> available)
    {
        var bySurface = available.ToLookup(model => model.Surface);
        return new
        {
            @object = "list",
            data = Order.Select(surface =>
            {
                var models = bySurface[surface].OrderBy(model => model.PublicName, StringComparer.Ordinal).ToArray();
                // The modalities of a type are what its models accept and return together (only some chat models see images).
                var shapes = (models.Length > 0 ? models : [new PublicModel(Guid.Empty, string.Empty, false, false, surface)])
                    .Select(model => JsonSerializer.SerializeToElement(From(model), Json)).ToArray();
                string[] Union(string property) => shapes
                    .SelectMany(shape => shape.GetProperty(property).EnumerateArray().Select(item => item.GetString()!))
                    .Distinct().ToArray();
                return new
                {
                    type = TypeOf(surface),
                    llmproxy_surface = surface.ToString(),
                    endpoints = Union("endpoints"),
                    input_modalities = Union("input_modalities"),
                    output_modalities = Union("output_modalities"),
                    model_count = models.Length,
                    models = models.Select(From).ToArray()
                };
            }).ToArray()
        };
    }
}
