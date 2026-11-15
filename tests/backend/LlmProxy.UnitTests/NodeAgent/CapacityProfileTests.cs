using LlmProxy.NodeAgent;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class CapacityProfileTests
{
    private static readonly NodeAgentOptions Options = NodeAgentOptions.From(new ConfigurationBuilder().Build());

    private static ManagedModelRecord Record(string runtime = "llama.cpp", int context = 32768, int sequences = 1, string[]? extra = null) => new()
    {
        InstallationId = "model-a", CatalogModelId = "custom:" + runtime, ProviderModelName = "org/model-GGUF:Q4_K_M", Runtime = runtime,
        Port = 18000, TensorParallelSize = 1, MaxModelLen = context, MaxNumSeqs = sequences, ExtraArguments = extra ?? [],
        ModelFile = "model.gguf", SourceRevision = new string('a', 40)
    };

    [Fact]
    public void An_embedding_batch_follows_the_context_per_slot_and_replaces_every_old_form()
    {
        var before = new[] { "--embedding", "--batch-size", "512", "--ubatch-size=512", "-b", "256", "--flag", "x" };

        var after = ManagedRuntimeProfiles.WithEmbeddingBatch("llama.cpp", before, 16384, 2);

        Assert.Equal(["--embedding", "--flag", "x", "--batch-size", "8192", "--ubatch-size", "8192"], after);
    }

    [Theory]
    [InlineData(300, 1, "512")]       // never below what llama.cpp needs for a normal input
    [InlineData(2048, 1, "2048")]
    [InlineData(4096, 1, "4096")]
    [InlineData(32768, 2, "8192")]    // never above the cap the installer uses
    [InlineData(65536, 1, "8192")]
    public void The_embedding_batch_uses_the_same_bounds_as_the_installer(int context, int sequences, string expected)
    {
        var after = ManagedRuntimeProfiles.WithEmbeddingBatch("llama.cpp", ["--embedding"], context, sequences);

        Assert.Equal(expected, after[after.ToList().IndexOf("--batch-size") + 1]);
        Assert.Equal(expected, after[after.ToList().IndexOf("--ubatch-size") + 1]);
    }

    [Fact]
    public void Only_a_llama_cpp_embedding_model_has_its_arguments_rewritten()
    {
        string[] chat = ["--jinja", "--batch-size", "64"];
        Assert.Same(chat, ManagedRuntimeProfiles.WithEmbeddingBatch("llama.cpp", chat, 65536, 1));
        string[] embedding = ["--embedding"];
        Assert.Same(embedding, ManagedRuntimeProfiles.WithEmbeddingBatch("vllm", embedding, 65536, 1));
    }

    [Fact]
    public void New_limits_reach_the_llama_cpp_command_line()
    {
        var record = ManagedRuntimeProfiles.WithLimits(Record(), new ManagedProfileUpdate(65536, 2, null));

        var arguments = ManagedRuntimeProfiles.Arguments(record, Options, gpuAvailable: false).ToList();

        Assert.Equal("65536", arguments[arguments.IndexOf("--ctx-size") + 1]);
        Assert.Equal("2", arguments[arguments.IndexOf("--parallel") + 1]);
    }

    [Fact]
    public void An_embedding_model_keeps_a_batch_that_matches_its_new_context_on_the_command_line()
    {
        var embedding = Record(context: 8192, sequences: 1, extra: ["--embedding", "--batch-size", "8192", "--ubatch-size", "8192"]);

        var arguments = ManagedRuntimeProfiles.Arguments(
            ManagedRuntimeProfiles.WithLimits(embedding, new ManagedProfileUpdate(2048, 1, null)), Options, gpuAvailable: false).ToList();

        Assert.Equal("2048", arguments[arguments.IndexOf("--batch-size") + 1]);
        Assert.Single(arguments, "--batch-size");
        Assert.Equal("2048", arguments[arguments.IndexOf("--ctx-size") + 1]);
    }

    [Fact]
    public void The_earlier_limits_come_back_whole_and_nothing_else_changes()
    {
        var original = Record(context: 32768, sequences: 1, extra: ["--embedding", "--batch-size", "8192", "--ubatch-size", "8192"]) with { Port = 18005 };
        var changed = ManagedRuntimeProfiles.WithLimits(original, new ManagedProfileUpdate(4096, 4, null)) with { Status = "error", Error = "out of memory" };

        var restored = ManagedRuntimeProfiles.RestoreLimits(changed, original);

        Assert.Equal(32768, restored.MaxModelLen);
        Assert.Equal(1, restored.MaxNumSeqs);
        Assert.Equal(original.ExtraArguments, restored.ExtraArguments);
        Assert.Equal("error", restored.Status);
        Assert.Equal(18005, restored.Port);
    }

    [Fact]
    public void A_value_the_engine_cannot_take_is_named_before_anything_is_touched()
    {
        Assert.Null(ManagedRuntimeProfiles.LimitsProblem(Record(), new ManagedProfileUpdate(131072, 4, null)));
        Assert.NotNull(ManagedRuntimeProfiles.LimitsProblem(Record(), new ManagedProfileUpdate(100, 1, null)));
        Assert.NotNull(ManagedRuntimeProfiles.LimitsProblem(Record(), new ManagedProfileUpdate(8192, 1, 256)));
        Assert.NotNull(ManagedRuntimeProfiles.LimitsProblem(Record(), new ManagedProfileUpdate(8192, 1, null, 0.7)));
        Assert.Null(ManagedRuntimeProfiles.LimitsProblem(Record("vllm"), new ManagedProfileUpdate(8192, 1, null, 0.7)));
    }
}
