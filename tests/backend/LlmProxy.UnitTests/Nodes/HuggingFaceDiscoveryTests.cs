using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Nodes;

public sealed class HuggingFaceDiscoveryTests
{
    [Theory]
    [InlineData("Qwen/Qwen3-8B", true)]
    [InlineData("org/my_model-v1", true)]
    [InlineData("HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive", true)]
    [InlineData("../private", false)]
    [InlineData("evil.com/path/extra", false)]
    [InlineData("https://evil.test/repo", false)]
    [InlineData("org/%2e%2e", false)]
    [InlineData("org/../../admin", false)]
    [InlineData("only-model-name", false)]
    public void Hub_repository_id_is_strictly_validated_before_network_access(
        string id, bool expected)
    {
        Assert.Equal(expected, HuggingFaceDiscoveryService.ValidRepository(id));
    }

    [Fact]
    public void Explicit_search_accepts_multimodal_gguf_models()
    {
        var url = HuggingFaceDiscoveryService.BuildDiscoverUrl(
            "HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive", "trending", 20);
        Assert.DoesNotContain("pipeline_tag=text-generation", url);
        Assert.Contains("search=HauhauCS%2FQwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive", url);
        Assert.Contains("pipeline_tag=text-generation",
            HuggingFaceDiscoveryService.BuildDiscoverUrl("", "trending", 20));
    }

    [Fact]
    public void Native_Qwen3_context_is_default_without_opt_in_YaRN_extension()
    {
        var qwen = DeployableModelCatalog.Find("qwen3-8b");
        var air = DeployableModelCatalog.Find("qwen3-8b-airllm");
        Assert.NotNull(qwen);
        Assert.NotNull(air);
        Assert.Equal(32768, qwen.ContextTokens);
        Assert.Equal(32768, air.ContextTokens);
        var caps = ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "airllm",
            maxModelLen: 16384, maxOutputTokens: 1024);
        Assert.Equal(16384, caps.ServingContextTokens);
        Assert.Equal(1024, caps.MaximumOutputTokens);
        Assert.Equal(131072, caps.ExtendedContextTokens);
    }
}
