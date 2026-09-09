namespace LlmProxy.Domain.Models;

public sealed class ModelDefinition
{
    private ModelDefinition()
    {
    }

    public ModelDefinition(
        string publicName,
        string providerModelName,
        bool supportsStreaming = true,
        bool supportsTools = true)
    {
        Update(publicName, providerModelName, supportsStreaming, supportsTools);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string PublicName { get; private set; } = string.Empty;
    public string ProviderModelName { get; private set; } = string.Empty;
    public bool SupportsStreaming { get; private set; } = true;
    public bool SupportsTools { get; private set; } = true;
    public bool Enabled { get; private set; } = true;

    public void Update(string publicName, string providerModelName, bool supportsStreaming, bool supportsTools)
    {
        if (string.IsNullOrWhiteSpace(publicName))
        {
            throw new ArgumentException("Public model name is required.", nameof(publicName));
        }

        if (string.IsNullOrWhiteSpace(providerModelName))
        {
            throw new ArgumentException("Provider model name is required.", nameof(providerModelName));
        }

        PublicName = publicName.Trim();
        ProviderModelName = providerModelName.Trim();
        SupportsStreaming = supportsStreaming;
        SupportsTools = supportsTools;
    }

    public void Enable() => Enabled = true;
    public void Disable() => Enabled = false;
}
