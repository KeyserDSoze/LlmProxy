using LlmProxy.Api.Admin;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ManagedKokoroCatalogTests
{
    [Fact]
    public void Kokoro_is_synthesis_only_and_not_a_chat_checkpoint()
    {
        var descriptor = DeployableModelCatalog.Find("kokoro-english-cpu");
        Assert.NotNull(descriptor);
        Assert.Equal("kokoro", descriptor.Runtime);
        Assert.Equal("hexgrad/Kokoro-82M", descriptor.ProviderModelName);
        Assert.Equal(ModelSurface.SpeechSynthesis, descriptor.Surface);
        Assert.False(descriptor.SupportsTools);
        Assert.False(descriptor.SupportsStreaming);
        Assert.Equal(0, descriptor.MinimumGpuCount);
        var model = new ModelDefinition(descriptor.Id, descriptor.ProviderModelName,
            descriptor.SupportsStreaming, descriptor.SupportsTools, descriptor.Surface);
        Assert.Equal(ModelSurface.SpeechSynthesis, model.Surface);

        var caps = ModelCapabilitiesCatalog.Resolve(descriptor.ProviderModelName, descriptor.Runtime);
        Assert.Equal("unsupported", caps.Chat.Serving);
        Assert.Null(caps.ServingContextTokens);
        Assert.Null(caps.AudioTranscription);
        Assert.Contains("/v1/audio/speech", caps.Notes);
    }
}
