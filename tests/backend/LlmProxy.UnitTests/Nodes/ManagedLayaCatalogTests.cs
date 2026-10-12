using LlmProxy.Api.Admin;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ManagedLayaCatalogTests
{
    [Fact]
    public void Laya_is_a_dedicated_non_chat_system_one_checkpoint()
    {
        var descriptor = DeployableModelCatalog.Find("laya-english-cpu");
        Assert.NotNull(descriptor);
        Assert.Equal("laya", descriptor.Runtime);
        Assert.Equal("convaiinnovations/laya", descriptor.ProviderModelName);
        Assert.Equal(ModelSurface.SystemOne, descriptor.Surface);
        Assert.Equal(0, descriptor.MinimumGpuCount);
        Assert.False(descriptor.SupportsStreaming);
        Assert.False(descriptor.SupportsTools);

        var logical = new ModelDefinition(descriptor.Id, descriptor.ProviderModelName,
            descriptor.SupportsStreaming, descriptor.SupportsTools, descriptor.Surface);
        Assert.Equal(ModelSurface.SystemOne, logical.Surface);
        var capabilities = ModelCapabilitiesCatalog.Resolve(descriptor.ProviderModelName, descriptor.Runtime);
        Assert.Equal("unsupported", capabilities.Chat.Serving);
        Assert.Null(capabilities.AudioTranscription);
        Assert.Null(capabilities.ServingContextTokens);
        Assert.Contains("/v1/systemone", capabilities.Notes);
    }
}
