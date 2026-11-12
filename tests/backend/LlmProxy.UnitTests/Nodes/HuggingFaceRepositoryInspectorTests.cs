using System.Text.Json;
using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Nodes;

public sealed class HuggingFaceRepositoryInspectorTests
{
    private static HuggingFaceRepositoryInspector.RepositoryInspection Inspect(string model, string? config = null)
    {
        using var document = JsonDocument.Parse(model);
        using var configDocument = config is null ? null : JsonDocument.Parse(config);
        return HuggingFaceRepositoryInspector.ParseInspection("org/model", document.RootElement.Clone(),
            configDocument?.RootElement.Clone());
    }

    [Fact]
    public void Safetensors_repository_reports_parameters_precision_size_context_and_suggests_vllm()
    {
        var result = Inspect("""
        {"id":"org/model","gated":false,"pipeline_tag":"text-generation","library_name":"transformers",
         "tags":["transformers","safetensors","qwen3","license:apache-2.0"],
         "config":{"architectures":["Qwen3ForCausalLM"],"model_type":"qwen3"},
         "safetensors":{"parameters":{"BF16":8190735360},"total":8190735360},
         "siblings":[
           {"rfilename":"config.json","size":700},
           {"rfilename":"model-00001-of-00002.safetensors","size":10000000000,"lfs":{"size":10000000000}},
           {"rfilename":"model-00002-of-00002.safetensors","size":6000000000,"lfs":{"size":6000000000}}]}
        """, """{"max_position_embeddings":40960}""");

        Assert.True(result.Found);
        Assert.False(result.Gated);
        Assert.Equal("apache-2.0", result.License);
        Assert.Equal("Qwen3ForCausalLM", result.Architecture);
        Assert.Equal(8190735360, result.ParameterCount);
        Assert.Equal("BF16", result.Precision);
        Assert.Equal(16_000_000_000, result.WeightsBytes);
        Assert.Equal(40960, result.ContextLength);
        Assert.True(result.HasSafetensors);
        Assert.False(result.HasGguf);
        Assert.Null(result.Quantization);
        Assert.Equal("vllm", result.Suggestion!.Runtime);
        Assert.Equal(32768, result.Suggestion.ContextTokens);
    }

    [Fact]
    public void Gguf_repository_lists_selectable_quantizations_and_suggests_llama_cpp()
    {
        var result = Inspect("""
        {"id":"org/model","gated":false,"pipeline_tag":"text-generation",
         "tags":["gguf","license:apache-2.0"],
         "gguf":{"total":8190735360,"architecture":"qwen3","context_length":40960},
         "siblings":[
           {"rfilename":"model-Q4_K_M.gguf","size":5000,"lfs":{"size":5000}},
           {"rfilename":"model-Q8_0.gguf","size":9000,"lfs":{"size":9000}},
           {"rfilename":"mmproj-F16.gguf","size":300,"lfs":{"size":300}}]}
        """);

        Assert.True(result.HasGguf);
        Assert.False(result.HasSafetensors);
        Assert.Equal("qwen3", result.Architecture);
        Assert.Equal(8190735360, result.ParameterCount);
        Assert.Equal(40960, result.ContextLength);
        Assert.Equal(new[] { "Q4_K_M", "Q8_0" }, result.GgufVariants.Select(item => item.Quantization));
        Assert.Equal("llama.cpp", result.Suggestion!.Runtime);
    }

    [Fact]
    public void Prequantized_repository_is_recognised_from_tags_and_config_json()
    {
        var byTags = Inspect("""{"tags":["safetensors","4-bit","awq"],"safetensors":{"parameters":{"BF16":1,"I32":9},"total":10}}""");
        Assert.Equal("awq", byTags.Quantization);
        Assert.Equal("I32 + BF16", byTags.Precision);
        Assert.Contains("AWQ", byTags.Suggestion!.Reason);

        var byConfig = Inspect("""{"tags":["safetensors","4-bit"]}""",
            """{"max_position_embeddings":4096,"quantization_config":{"quant_method":"GPTQ"}}""");
        Assert.Equal("gptq", byConfig.Quantization);
    }

    [Fact]
    public void Multimodal_context_lives_under_text_config()
    {
        var result = Inspect("""{"pipeline_tag":"image-text-to-text","tags":["safetensors"]}""",
            """{"text_config":{"max_position_embeddings":131072},"vision_config":{}}""");
        Assert.True(result.Multimodal);
        Assert.Equal(131072, result.ContextLength);
        Assert.Equal(32768, result.Suggestion!.ContextTokens);
    }

    [Theory]
    [InlineData("""{"gated":"manual","tags":["safetensors"]}""", true)]
    [InlineData("""{"gated":true,"tags":["safetensors"]}""", true)]
    [InlineData("""{"gated":false,"tags":["safetensors"]}""", false)]
    [InlineData("""{"private":true,"tags":["safetensors"]}""", true)]
    public void Gated_and_private_repositories_are_flagged(string json, bool gated)
    {
        Assert.Equal(gated, Inspect(json).Gated);
    }

    [Fact]
    public void Repository_without_a_serveable_format_has_no_suggestion()
    {
        var result = Inspect("""{"tags":["onnx"],"siblings":[{"rfilename":"model.onnx","size":1}]}""");
        Assert.Null(result.Suggestion);
        Assert.Null(result.WeightsBytes);
    }

    [Theory]
    [InlineData("""{"max_position_embeddings":8192}""", 8192)]
    [InlineData("""{"max_sequence_length":2048}""", 2048)]
    [InlineData("""{"max_position_embeddings":12}""", null)]
    [InlineData("""{"max_position_embeddings":4000000000}""", null)]
    [InlineData("""{"hidden_size":4096}""", null)]
    public void Context_window_is_read_defensively(string json, int? expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, HuggingFaceRepositoryInspector.ReadContext(document.RootElement));
    }

    [Theory]
    [InlineData("gguf,text-generation", "gguf")]
    [InlineData("awq,4-bit", "awq")]
    [InlineData("4-bit,bitsandbytes", "bitsandbytes")]
    [InlineData("fp8,safetensors", "fp8")]
    [InlineData("safetensors,transformers", null)]
    public void Quantization_format_prefers_the_most_specific_tag(string tags, string? expected)
    {
        Assert.Equal(expected, HuggingFaceRepositoryInspector.FormatFromTags(tags.Split(',')));
    }

    [Fact]
    public void Quantized_builds_are_classified_and_unrelated_or_invalid_items_are_dropped()
    {
        using var document = JsonDocument.Parse("""
        [
         {"id":"Qwen/Qwen3-8B-AWQ","downloads":1455852,"likes":70,"lastModified":"2025-05-21T06:09:42.000Z",
          "tags":["safetensors","4-bit","awq"],"safetensors":{"total":8190735360}},
         {"id":"unsloth/Qwen3-8B-GGUF","downloads":173042,"likes":161,
          "tags":["gguf","base_model:quantized:Qwen/Qwen3-8B"],"gguf":{"total":8190735360}},
         {"id":"Qwen/Qwen3-8B-FP8","downloads":268195,"likes":69,"tags":["fp8"]},
         {"id":"org/plain-finetune","downloads":5,"likes":0,"tags":["safetensors"]},
         {"id":"../evil","downloads":1,"likes":0,"tags":["gguf"]},
         {"downloads":1}
        ]
        """);

        var builds = HuggingFaceRepositoryInspector.ParseDerivatives(document.RootElement);

        Assert.Equal(new[] { "Qwen/Qwen3-8B-AWQ", "unsloth/Qwen3-8B-GGUF", "Qwen/Qwen3-8B-FP8" }, builds.Select(item => item.Id));
        Assert.Equal(new[] { "awq", "gguf", "fp8" }, builds.Select(item => item.Format));
        Assert.Equal(1455852, builds[0].Downloads);
        Assert.Equal(8190735360, builds[1].ParameterCount);
        Assert.Null(builds[2].ParameterCount);
        Assert.Equal(new DateTimeOffset(2025, 5, 21, 6, 9, 42, TimeSpan.Zero), builds[0].LastModified);
        using var notAList = JsonDocument.Parse("{}");
        Assert.Empty(HuggingFaceRepositoryInspector.ParseDerivatives(notAList.RootElement));
    }
}
