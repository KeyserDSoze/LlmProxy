using LlmProxy.Api.Admin;
using LlmProxy.Domain.Models;
using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.Api;

public sealed class EmbeddingCatalogTests
{
    private static IReadOnlyList<DeployableModelDescriptor> Embeddings => DeployableModelCatalog.All.Where(item => item.Surface == ModelSurface.Embedding).ToList();

    [Fact]
    public void The_catalog_offers_curated_embedding_models_served_by_llama_cpp()
    {
        Assert.True(Embeddings.Count >= 3);
        Assert.All(Embeddings, item =>
        {
            Assert.Equal("llama.cpp", item.Runtime);
            Assert.Contains("--embedding", item.LaunchArguments ?? []);
            Assert.False(item.SupportsStreaming);
            Assert.False(item.SupportsTools);
            Assert.Contains("embeddings", item.Tags);
            // The Agent resolves the GGUF file from repo:QUANT, so the pinned name must parse.
            var (repository, quantization) = GgufFileSelector.SplitRepositoryTag(item.ProviderModelName);
            Assert.Contains('/', repository);
            Assert.NotNull(quantization);
        });
        Assert.Equal(Embeddings.Count, Embeddings.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void Embedding_models_start_with_embedding_mode_and_a_batch_big_enough_for_the_longest_input()
    {
        var descriptor = DeployableModelCatalog.Find("qwen3-embedding-0.6b-gguf")!;

        var args = NodeModelManagementEndpoints.LaunchArguments(descriptor,
            new NodeModelManagementEndpoints.InstallManagedModelRequest(MaxModelLen: 16384, MaxNumSeqs: 2, ExtraArguments: ["--threads", "4"]));

        Assert.Equal(["--embedding", "--batch-size", "8192", "--ubatch-size", "8192", "--threads", "4"], args);
    }

    [Fact]
    public void A_small_context_still_gets_a_usable_batch_and_the_operator_cannot_repeat_a_managed_flag()
    {
        var descriptor = DeployableModelCatalog.Find("nomic-embed-text-v1.5-gguf")!;

        var args = NodeModelManagementEndpoints.LaunchArguments(descriptor,
            new NodeModelManagementEndpoints.InstallManagedModelRequest(MaxModelLen: 2048, ExtraArguments: ["--embedding"]));

        Assert.Equal(["--embedding", "--batch-size", "2048", "--ubatch-size", "2048"], args);
    }

    [Fact]
    public void Chat_models_get_exactly_the_operators_arguments()
    {
        var descriptor = DeployableModelCatalog.Find("qwen3-8b")!;

        Assert.Equal(["--max-num-batched-tokens", "4096"], NodeModelManagementEndpoints.LaunchArguments(descriptor,
            new NodeModelManagementEndpoints.InstallManagedModelRequest(ExtraArguments: ["--max-num-batched-tokens", "4096"])));
    }

    [Fact]
    public void An_embedding_model_is_never_described_as_a_chat_model()
    {
        var profile = ModelCapabilitiesCatalog.ForSurface(
            ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-Embedding-0.6B-GGUF:Q8_0", "llama.cpp"), ModelSurface.Embedding);

        Assert.Equal("unsupported", profile.Chat.Serving);
        Assert.Equal("unsupported", profile.ToolCalling.Model);
        Assert.Equal("unsupported", profile.Vision.Model);
        Assert.Contains("/v1/embeddings", profile.Notes);
        Assert.Same(ModelCapabilitiesCatalog.Resolve("Qwen/Qwen3-8B", "vllm").Chat.GetType(), profile.Chat.GetType());
    }

    private static NodeModelManagementEndpoints.ModelInstallJob Job(string catalogId) => new(Guid.NewGuid(),
        new NodeModelManagementEndpoints.AgentInstallRequest(catalogId, "org/model", null, 1, [], "llama.cpp", null, null, null, null), "completed", "complete", 100, "done",
        null, null, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void A_job_is_shown_under_the_kind_of_model_it_installs()
    {
        Assert.Equal("Embedding", NodeModelManagementEndpoints.SurfaceOf(Job("qwen3-embedding-0.6b-gguf"), null));
        Assert.Equal("SpeechSynthesis", NodeModelManagementEndpoints.SurfaceOf(Job("kokoro-english-cpu"), null));
        Assert.Equal("OpenAi", NodeModelManagementEndpoints.SurfaceOf(Job("custom"), null));
        Assert.Equal("Embedding", NodeModelManagementEndpoints.SurfaceOf(Job("custom"), "{\"surface\":\"Embedding\"}"));
        Assert.Equal("OpenAi", NodeModelManagementEndpoints.SurfaceOf(Job("custom"), "not json"));
    }
}
