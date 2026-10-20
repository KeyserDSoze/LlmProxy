using LlmProxy.Api.Admin;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Nodes;

public sealed class LiquidLfm25CatalogTests
{
    [Theory]
    [InlineData("lfm25-24b-a2b-bf16", "LiquidAI/LFM2.5-24B-A2B", "vllm")]
    [InlineData("lfm25-24b-a2b-fp8", "LiquidAI/LFM2.5-24B-A2B-FP8", "vllm")]
    [InlineData("lfm25-24b-a2b-gguf-q4", "LiquidAI/LFM2.5-24B-A2B-GGUF:Q4_K_M", "llama.cpp")]
    public void Lfm25_descriptors_and_capabilities_are_separate_from_runtime_approval(string id, string provider, string runtime)
    {
        var descriptor = DeployableModelCatalog.Find(id);
        Assert.NotNull(descriptor);
        Assert.Equal(provider, descriptor.ProviderModelName);
        Assert.Equal(runtime, descriptor.Runtime);
        Assert.Equal(131072, descriptor.ContextTokens);
        Assert.Equal(ModelSurface.OpenAi, descriptor.Surface);
        Assert.Equal("LFM Open License 1.0", descriptor.License);
        Assert.True(descriptor.SupportsStreaming);
        Assert.True(descriptor.SupportsTools);

        var caps = ModelCapabilitiesCatalog.Resolve(provider, runtime, maxModelLen: 65536);
        Assert.Equal(131072, caps.NativeContextTokens);
        Assert.Equal(65536, caps.ServingContextTokens);
        Assert.Equal("supported", caps.Chat.Model);
        Assert.Equal("supported", caps.ToolCalling.Model);
        Assert.Equal("unverified", caps.ToolCalling.Serving);
        Assert.Equal("supported", caps.Thinking.Model);
        Assert.Equal("partial", caps.Thinking.Serving);
        Assert.Equal("unsupported", caps.Vision.Model);
        Assert.Equal("supported", caps.Streaming.Serving);
    }

    [Fact]
    public void Model_id_matching_is_exact()
    {
        var unverified = ModelCapabilitiesCatalog.Resolve("Someone/LFM2.5-24B-A2B", "vllm");
        Assert.Equal("unknown", unverified.Chat.Model);
        Assert.Equal("unknown", unverified.ToolCalling.Model);
        Assert.Null(unverified.NativeContextTokens);
    }
}
