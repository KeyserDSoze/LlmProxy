namespace LlmProxy.Domain.Models;

public enum ModelSurface
{
    OpenAi = 0,
    SystemOne = 1,
    ImageGeneration = 2,
    SpeechSynthesis = 3,
    AudioTranscription = 4,
    /// <summary>OpenAI-style <c>POST /v1/embeddings</c>.</summary>
    Embedding = 5,
    /// <summary>Streaming voice (OpenAI-style realtime WebSocket); prepared, served only by a runtime that speaks it.</summary>
    Realtime = 6
}

public sealed class ModelDefinition
{
    private ModelDefinition()
    {
    }

    public ModelDefinition(
        string publicName,
        string providerModelName,
        bool supportsStreaming = true,
        bool supportsTools = true,
        ModelSurface surface = ModelSurface.OpenAi)
    {
        Update(publicName, providerModelName, supportsStreaming, supportsTools, surface);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string PublicName { get; private set; } = string.Empty;
    public string ProviderModelName { get; private set; } = string.Empty;
    public bool SupportsStreaming { get; private set; } = true;
    public bool SupportsTools { get; private set; } = true;
    public bool SupportsVision { get; private set; }
    public bool SupportsReasoning { get; private set; }

    /// <summary>Largest prompt plus completion the operator declares for this model; null when not declared.</summary>
    public int? ContextWindowTokens { get; private set; }

    /// <summary>Largest completion a client may ask for; null when not declared.</summary>
    public int? MaxOutputTokens { get; private set; }
    public ModelSurface Surface { get; private set; } = ModelSurface.OpenAi;
    public bool Enabled { get; private set; } = true;

    public void Update(string publicName, string providerModelName, bool supportsStreaming, bool supportsTools, ModelSurface surface = ModelSurface.OpenAi)
    {
        if (string.IsNullOrWhiteSpace(publicName))
        {
            throw new ArgumentException("Public model name is required.", nameof(publicName));
        }

        if (string.IsNullOrWhiteSpace(providerModelName))
        {
            throw new ArgumentException("Provider model name is required.", nameof(providerModelName));
        }

        if (!Enum.IsDefined(surface))
        {
            throw new ArgumentOutOfRangeException(nameof(surface), "Unknown inference API surface.");
        }

        PublicName = publicName.Trim();
        ProviderModelName = providerModelName.Trim();
        SupportsStreaming = supportsStreaming;
        SupportsTools = supportsTools;
        Surface = surface;
    }

    /// <summary>
    /// Declared properties clients can read from the model details endpoint. They describe what the operator expects
    /// the model to do; they never block or rewrite a request.
    /// </summary>
    public void UpdateCapabilities(
        bool supportsStreaming, bool supportsTools, bool supportsVision, bool supportsReasoning,
        int? contextWindowTokens, int? maxOutputTokens)
    {
        if (contextWindowTokens is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(contextWindowTokens), "Context window must be between 1 and 10,000,000 tokens.");
        if (maxOutputTokens is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), "Maximum output must be between 1 and 10,000,000 tokens.");
        if (contextWindowTokens is { } window && maxOutputTokens is { } output && output > window)
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), "Maximum output cannot exceed the context window.");

        SupportsStreaming = supportsStreaming;
        SupportsTools = supportsTools;
        SupportsVision = supportsVision;
        SupportsReasoning = supportsReasoning;
        ContextWindowTokens = contextWindowTokens;
        MaxOutputTokens = maxOutputTokens;
    }

    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;
}
