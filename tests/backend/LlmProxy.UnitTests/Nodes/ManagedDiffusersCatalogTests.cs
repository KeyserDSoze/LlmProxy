using LlmProxy.Api.Admin;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ManagedDiffusersCatalogTests
{
    [Fact]
    public void Ssd1b_is_a_gpu_only_image_model_not_a_chat_model()
    {
        var descriptor = DeployableModelCatalog.Find("ssd-1b-image");
        Assert.NotNull(descriptor);
        Assert.Equal("diffusers", descriptor.Runtime);
        Assert.Equal("segmind/SSD-1B", descriptor.ProviderModelName);
        Assert.Equal(ModelSurface.ImageGeneration, descriptor.Surface);
        Assert.Equal(1, descriptor.MinimumGpuCount);
        Assert.True(descriptor.MinimumGpuMemoryGiB > 0);
        Assert.False(descriptor.SupportsTools);
        Assert.False(descriptor.SupportsStreaming);
        var model = new ModelDefinition(descriptor.Id, descriptor.ProviderModelName,
            descriptor.SupportsStreaming, descriptor.SupportsTools, descriptor.Surface);
        Assert.Equal(ModelSurface.ImageGeneration, model.Surface);
        Assert.NotEqual(ModelSurface.OpenAi, model.Surface);
        Assert.NotEqual(ModelSurface.AudioTranscription, model.Surface);

        var capabilities = ModelCapabilitiesCatalog.Resolve(descriptor.ProviderModelName, descriptor.Runtime);
        Assert.Equal("unsupported", capabilities.Chat.Serving);
        Assert.Null(capabilities.AudioTranscription);
        Assert.Null(capabilities.ServingContextTokens);
        Assert.Contains("/v1/images/generations", capabilities.Notes);
    }
}
