using LlmProxy.Api.Admin;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ManagedWhisperCatalogTests
{
    [Theory]
    [InlineData("whisper-tiny-cpu", "Systran/faster-whisper-tiny")]
    [InlineData("whisper-base-cpu", "Systran/faster-whisper-base")]
    [InlineData("whisper-small-cpu", "Systran/faster-whisper-small")]
    public void Whisper_checkpoint_has_audio_only_model_surface(string id, string repo)
    {
        var descriptor = DeployableModelCatalog.Find(id);
        Assert.NotNull(descriptor);
        Assert.Equal(ModelSurface.AudioTranscription, descriptor.Surface);
        Assert.Equal("faster-whisper", descriptor.Runtime);
        Assert.Equal(repo, descriptor.ProviderModelName);
        Assert.Equal(0, descriptor.MinimumGpuCount);
        Assert.Equal(0, descriptor.MinimumGpuMemoryGiB);
        Assert.False(descriptor.SupportsTools);
        Assert.False(descriptor.SupportsStreaming);

        var published = new ModelDefinition(id, repo, descriptor.SupportsStreaming,
            descriptor.SupportsTools, descriptor.Surface);
        Assert.Equal(ModelSurface.AudioTranscription, published.Surface);

        var capabilities = ModelCapabilitiesCatalog.Resolve(repo, descriptor.Runtime);
        Assert.Equal("unsupported", capabilities.Chat.Serving);
        Assert.Equal("supported", capabilities.AudioTranscription?.Serving);
        Assert.Null(capabilities.ServingContextTokens); // Not a Chat token window.
    }

    [Fact]
    public void Chat_and_speech_repositories_do_not_share_a_surface()
    {
        Assert.Equal(ModelSurface.OpenAi, DeployableModelCatalog.Find("qwen3-8b")!.Surface);
        Assert.NotEqual(DeployableModelCatalog.Find("qwen3-8b")!.Surface,
            DeployableModelCatalog.Find("whisper-tiny-cpu")!.Surface);
    }
}
