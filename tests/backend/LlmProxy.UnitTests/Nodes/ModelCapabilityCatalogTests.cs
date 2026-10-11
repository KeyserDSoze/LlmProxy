using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ModelCapabilityCatalogTests
{
    [Fact]
    public void Qwen3_8b_on_AirLLM_never_advertises_unsupported_client_capabilities()
    {
        var profile = ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "airllm");
        Assert.Equal("supported", profile.Chat.Serving);
        Assert.Equal("supported", profile.ToolCalling.Model);
        Assert.Equal("unsupported", profile.ToolCalling.Serving);
        Assert.Equal("unsupported", profile.Streaming.Serving);
        Assert.Equal("unsupported", profile.Vision.Model);
        Assert.Equal("unsupported", profile.Vision.Serving);
        Assert.Equal("supported", profile.Thinking.Model);
        Assert.Equal("partial", profile.Thinking.Serving);
        Assert.Equal(32768, profile.NativeContextTokens);
        Assert.Equal(131072, profile.ExtendedContextTokens);
        Assert.Equal(8192, profile.ServingContextTokens);
        Assert.Equal(256, profile.MaximumOutputTokens);
    }

    [Fact]
    public void Configured_AirLLM_context_overrides_default_without_inventing_output_capacity()
    {
        var profile = ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "airllm", 4096);
        Assert.Equal(4096, profile.ServingContextTokens);
        Assert.Equal(256, profile.MaximumOutputTokens);
    }

    [Fact]
    public void Moving_the_same_Qwen_model_to_vLLM_changes_API_capabilities_without_changing_model_facts()
    {
        var profile = ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "vllm", 8192);
        Assert.Equal("supported", profile.ToolCalling.Model);
        Assert.Equal("unverified", profile.ToolCalling.Serving); // Parser not enabled automatically.
        Assert.Equal("supported", profile.Streaming.Serving);
        Assert.Equal("unsupported", profile.Vision.Serving);
        Assert.Null(profile.MaximumOutputTokens);
        Assert.Equal(8192, profile.ServingContextTokens);
    }

    [Fact]
    public void Unknown_custom_checkpoint_is_not_guess_assigned_vision_tools_or_thinking()
    {
        var profile = ModelCapabilitiesCatalog.Resolve("UnknownOrganisation/PrivateModel", "airllm");
        Assert.Equal("unknown", profile.ToolCalling.Model);
        Assert.Equal("unsupported", profile.ToolCalling.Serving);
        Assert.Equal("unknown", profile.Vision.Model);
        Assert.Equal("unsupported", profile.Vision.Serving);
        Assert.Equal("unknown", profile.Thinking.Model);
        Assert.Null(profile.NativeContextTokens);
        Assert.Equal(256, profile.MaximumOutputTokens);
    }

    [Fact]
    public void Source_model_capabilities_are_unrelated_to_GB10_hardware_sizing_or_FITS()
    {
        var air = ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "airllm");
        var vllm = ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "vllm");
        Assert.Equal(air.NativeContextTokens, vllm.NativeContextTokens);
        Assert.NotEqual(air.Streaming.Serving, vllm.Streaming.Serving);
    }
}
