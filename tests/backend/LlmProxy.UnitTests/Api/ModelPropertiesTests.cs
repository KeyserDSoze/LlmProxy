using System.Text.Json;
using LlmProxy.Api.Admin;
using LlmProxy.Api.OpenAi;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Api;

public sealed class ModelPropertiesTests
{
    [Theory]
    [InlineData("https://huggingface.co/meta-models/Muse-Glimmer-30B", "meta-models/Muse-Glimmer-30B", null)]
    [InlineData("https://huggingface.co/meta-models/Muse-Glimmer-30B/", "meta-models/Muse-Glimmer-30B", null)]
    [InlineData("  https://www.huggingface.co/meta-models/Muse-Glimmer-30B/tree/main?show_file_info=x  ", "meta-models/Muse-Glimmer-30B", null)]
    [InlineData("huggingface.co/Qwen/Qwen3-8B", "Qwen/Qwen3-8B", null)]
    [InlineData("hf.co/Qwen/Qwen3-8B", "Qwen/Qwen3-8B", null)]
    [InlineData("hf://Qwen/Qwen3-8B", "Qwen/Qwen3-8B", null)]
    [InlineData("Qwen/Qwen3-8B", "Qwen/Qwen3-8B", null)]
    [InlineData("meta-models/Muse-Glimmer-30B-GGUF:Q4_K_M", "meta-models/Muse-Glimmer-30B-GGUF", "Q4_K_M")]
    [InlineData("https://huggingface.co/meta-models/Muse-Glimmer-30B-GGUF/blob/main/Muse-Glimmer-30B-Q4_K_M.gguf", "meta-models/Muse-Glimmer-30B-GGUF", "Q4_K_M")]
    public void A_pasted_link_becomes_a_repository_and_quantization(string input, string repository, string? quantization)
    {
        var reference = HuggingFaceRepositoryReference.Parse(input);

        Assert.NotNull(reference);
        Assert.Equal(repository, reference.Repository);
        Assert.Equal(quantization, reference.Quantization);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/org/model")]
    [InlineData("https://huggingface.co/")]
    [InlineData("https://huggingface.co/models")]
    [InlineData("just-a-word")]
    [InlineData("org/../etc")]
    [InlineData("ftp://huggingface.co/org/model")]
    public void Anything_else_is_refused(string input) => Assert.Null(HuggingFaceRepositoryReference.Parse(input));

    private static readonly string[] MuseTags = ["transformers", "safetensors", "muse_glimmer", "image-text-to-text", "conversational"];
    private const string MuseTemplate = "{# ATEM tool-calling protocol: reasoning channel (to=self), tool channels, tool definitions #}{%- if tools -%}...{%- endif -%}";

    [Fact]
    public void Muse_Glimmer_is_detected_as_vision_tools_and_reasoning_from_its_own_metadata()
    {
        var detected = ModelFeatureHints.FromHub(MuseTags, "image-text-to-text", MuseTemplate, hasVisionConfig: true, hasMmprojFile: false, 131072);

        Assert.True(detected.Tools);
        Assert.True(detected.Vision);
        Assert.True(detected.Reasoning);
        Assert.Equal(131072, detected.ContextWindow);
        Assert.NotEmpty(detected.Evidence);
    }

    [Fact]
    public void A_plain_text_model_is_not_credited_with_features_it_does_not_declare()
    {
        var detected = ModelFeatureHints.FromHub(["transformers", "text-generation"], "text-generation", "{{ messages }}", false, false, 8192);

        Assert.False(detected.Tools);
        Assert.False(detected.Vision);
        Assert.False(detected.Reasoning);
    }

    [Fact]
    public void A_projector_file_means_image_input_for_gguf_repositories()
        => Assert.True(ModelFeatureHints.FromHub(["gguf"], null, null, false, hasMmprojFile: true, null).Vision);

    [Fact]
    public void The_chat_template_is_read_from_config_or_gguf_metadata()
    {
        using var safetensors = JsonDocument.Parse("""{"config":{"chat_template_jinja":"tools here"}}""");
        using var nested = JsonDocument.Parse("""{"config":{"tokenizer_config":{"chat_template":[{"name":"default","template":"tool_calls"}]}}}""");
        using var gguf = JsonDocument.Parse("""{"gguf":{"chat_template":"<think>"}}""");

        Assert.Equal("tools here", ModelFeatureHints.ChatTemplate(safetensors.RootElement));
        Assert.Equal("tool_calls", ModelFeatureHints.ChatTemplate(nested.RootElement));
        Assert.Equal("<think>", ModelFeatureHints.ChatTemplate(gguf.RootElement));
        Assert.Null(ModelFeatureHints.ChatTemplate(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void A_checkpoint_the_catalog_does_not_know_takes_its_features_from_the_hub_declaration()
    {
        var hints = ModelFeatureHints.FromHub(MuseTags, "image-text-to-text", MuseTemplate, true, false, 131072);

        var profile = ModelCapabilitiesCatalog.WithHubHints(
            ModelCapabilitiesCatalog.Resolve("meta-models/Muse-Glimmer-30B", "vllm"), hints);

        Assert.Equal("supported", profile.ToolCalling.Model);
        Assert.Equal("unverified", profile.ToolCalling.Serving);
        Assert.Equal("supported", profile.Vision.Model);
        Assert.Equal(131072, profile.NativeContextTokens);
        Assert.Contains("Hugging Face", profile.Source);
    }

    [Fact]
    public void Curated_models_are_not_overridden_by_hub_hints()
    {
        var curated = ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "vllm");
        Assert.Same(curated, ModelCapabilitiesCatalog.WithHubHints(curated, ModelFeatureHints.FromHub(MuseTags, "image-text-to-text", MuseTemplate, true, false, 1)));
    }

    [Fact]
    public void Suggested_properties_for_a_vision_tool_model_on_vllm()
    {
        var hints = ModelFeatureHints.FromHub(MuseTags, "image-text-to-text", MuseTemplate, true, false, 131072);

        var suggestion = ModelPropertyResolver.Suggest("meta-models/Muse-Glimmer-30B", "vllm", hints, 32768, 4, null);

        Assert.True(suggestion.Properties.Streaming);
        Assert.True(suggestion.Properties.Tools);
        Assert.True(suggestion.Properties.Vision);
        Assert.True(suggestion.Properties.Reasoning);
        Assert.Equal(32768, suggestion.Properties.ContextWindow);
        Assert.Equal(8192, suggestion.Properties.MaxOutputTokens);
        Assert.Contains("Hugging Face", suggestion.Source);
    }

    [Fact]
    public void Llama_cpp_shares_its_context_pool_between_slots()
    {
        var suggestion = ModelPropertyResolver.Suggest("org/model-GGUF:Q4_K_M", "llama.cpp", null, 32768, 4, null);

        Assert.Equal(8192, suggestion.Properties.ContextWindow);
        Assert.Equal(2048, suggestion.Properties.MaxOutputTokens);
    }

    [Fact]
    public void AirLLM_never_claims_streaming_or_tools_and_keeps_its_output_cap()
    {
        var suggestion = ModelPropertyResolver.Suggest("Qwen/Qwen3-8B", "airllm", null, 8192, 1, 256);

        Assert.False(suggestion.Properties.Streaming);
        Assert.False(suggestion.Properties.Tools);
        Assert.Equal(256, suggestion.Properties.MaxOutputTokens);
    }

    [Fact]
    public void Nothing_is_claimed_for_a_checkpoint_without_any_published_information()
    {
        var suggestion = ModelPropertyResolver.Suggest("someone/unknown-model", "vllm", null, 8192, 1, null);

        Assert.False(suggestion.Properties.Tools);
        Assert.False(suggestion.Properties.Vision);
        Assert.False(suggestion.Properties.Reasoning);
        Assert.True(suggestion.Properties.Streaming);
        Assert.Contains("review", suggestion.Source);
    }

    [Fact]
    public void What_the_operator_declares_wins_over_the_suggestion()
    {
        var suggested = new ModelPropertyResolver.ModelProperties(true, false, false, false, 8192, 2048);

        var final = ModelPropertyResolver.Apply(suggested, new ModelPropertyResolver.DeclaredProperties(Tools: true, ContextWindow: 65536));

        Assert.True(final.Tools);
        Assert.Equal(65536, final.ContextWindow);
        Assert.Equal(2048, final.MaxOutputTokens);
        Assert.True(final.Streaming);
    }

    [Fact]
    public void Model_properties_validate_their_limits()
    {
        var model = new ModelDefinition("alias", "org/model");

        model.UpdateCapabilities(true, true, true, false, 131072, 8192);
        Assert.Equal(131072, model.ContextWindowTokens);
        Assert.True(model.SupportsVision);
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateCapabilities(true, true, false, false, 4096, 8192));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateCapabilities(true, true, false, false, 0, null));
        model.UpdateCapabilities(true, false, false, false, null, null);
        Assert.Null(model.ContextWindowTokens);
    }

    [Fact]
    public void The_details_endpoint_describes_each_model_for_clients()
    {
        var chat = new PublicModel(Guid.NewGuid(), "muse", true, true, ModelSurface.OpenAi, SupportsVision: true, SupportsReasoning: true,
            ContextWindowTokens: 131072, MaxOutputTokens: 8192);
        var speech = new PublicModel(Guid.NewGuid(), "voice", false, false, ModelSurface.SpeechSynthesis);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[] { ModelDetails.From(chat), ModelDetails.From(speech) }, ModelDetails.Json));
        var rows = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal("muse", rows[0].GetProperty("id").GetString());
        Assert.Equal(131072, rows[0].GetProperty("context_window").GetInt32());
        Assert.Equal(8192, rows[0].GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(["text", "image"], rows[0].GetProperty("input_modalities").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.True(rows[0].GetProperty("capabilities").GetProperty("vision").GetBoolean());
        Assert.True(rows[0].GetProperty("capabilities").GetProperty("reasoning").GetBoolean());
        Assert.Contains("/v1/chat/completions", rows[0].GetProperty("endpoints").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("context_window").ValueKind);
        Assert.Equal(["audio"], rows[1].GetProperty("output_modalities").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.False(rows[1].GetProperty("capabilities").GetProperty("tools").GetBoolean());
    }
}
