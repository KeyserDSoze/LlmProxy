using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class ModelFamilyProfileTests
{
    [Theory]
    [InlineData("meta-models/Muse-Glimmer-30B", "muse-glimmer")]
    [InlineData("meta-models/Muse-Glimmer-30B-GGUF:Q4_K_M", "muse-glimmer")]
    [InlineData("unsloth/Muse-Glimmer-30B-GGUF", "muse-glimmer")]
    [InlineData("Qwen/Qwen3-8B", "qwen3")]
    [InlineData("Qwen/Qwen3-4B-AWQ", "qwen3")]
    [InlineData("Qwen/Qwen3-32B", "qwen3")]
    public void Known_families_are_recognised_from_the_repository_name(string model, string family)
        => Assert.Equal(family, ModelFamilyProfile.Find(model)?.Id);

    [Theory]
    [InlineData("Qwen/Qwen3-Coder-30B-A3B-Instruct")]
    [InlineData("Qwen/Qwen3-Next-80B-A3B-Instruct")]
    [InlineData("Qwen/Qwen3.5-122B")]
    [InlineData("Qwen/Qwen2.5-7B-Instruct")]
    [InlineData("LiquidAI/LFM2.5-24B-A2B")]
    [InlineData("org/unknown-model")]
    [InlineData("")]
    public void Look_alikes_and_unknown_models_get_no_profile_because_their_parsers_differ(string model)
        => Assert.Null(ModelFamilyProfile.Find(model));

    [Fact]
    public void Muse_glimmer_on_vllm_names_both_parsers_and_reads_the_published_sampling()
    {
        var args = ModelFamilyProfile.ArgumentsFor("meta-models/Muse-Glimmer-30B", "vllm", [], enabled: true);

        Assert.Contains("--enable-auto-tool-choice", args);
        AssertPair(args, "--tool-call-parser", "muse_glimmer");
        AssertPair(args, "--reasoning-parser", "muse_glimmer");
        AssertPair(args, "--generation-config", "auto");
    }

    [Fact]
    public void Muse_glimmer_on_llama_cpp_applies_the_model_card_sampling_that_llama_cpp_does_not_read_itself()
    {
        var args = ModelFamilyProfile.ArgumentsFor("meta-models/Muse-Glimmer-30B-GGUF:Q4_K_M", "llama.cpp", [], enabled: true);

        AssertPair(args, "--temp", "1.0");
        AssertPair(args, "--top-p", "0.95");
        AssertPair(args, "--top-k", "64");
    }

    [Fact]
    public void A_flag_the_operator_already_set_is_not_added_again_and_is_reported_as_overridden()
    {
        string[] operatorArguments = ["--reasoning-parser", "custom", "--temp=0.2"];

        var vllm = ModelFamilyProfile.ArgumentsFor("meta-models/Muse-Glimmer-30B", "vllm", operatorArguments, enabled: true);
        var llama = ModelFamilyProfile.ArgumentsFor("meta-models/Muse-Glimmer-30B-GGUF", "llama.cpp", operatorArguments, enabled: true);

        Assert.DoesNotContain("--reasoning-parser", vllm);
        Assert.Contains("--tool-call-parser", vllm);
        Assert.DoesNotContain("--temp", llama);
        Assert.Contains("--top-k", llama);

        var info = ModelFamilyProfile.Describe("meta-models/Muse-Glimmer-30B", "vllm", operatorArguments, enabled: true)!;
        Assert.Equal(["--reasoning-parser"], info.OverriddenByOperator);
    }

    [Fact]
    public void Qwen3_gets_vllm_parsers_but_nothing_is_invented_for_runtimes_without_a_verified_recipe()
    {
        var vllm = ModelFamilyProfile.ArgumentsFor("Qwen/Qwen3-8B", "vllm", [], enabled: true);
        AssertPair(vllm, "--tool-call-parser", "hermes");
        AssertPair(vllm, "--reasoning-parser", "qwen3");

        Assert.Empty(ModelFamilyProfile.ArgumentsFor("Qwen/Qwen3-8B", "sglang", [], enabled: true));
        Assert.Empty(ModelFamilyProfile.ArgumentsFor("Qwen/Qwen3-8B", "llama.cpp", [], enabled: true));
    }

    [Fact]
    public void The_switch_turns_every_profile_off_and_the_state_says_so()
    {
        Assert.Empty(ModelFamilyProfile.ArgumentsFor("meta-models/Muse-Glimmer-30B", "vllm", [], enabled: false));
        var info = ModelFamilyProfile.Describe("meta-models/Muse-Glimmer-30B", "vllm", [], enabled: false)!;
        Assert.False(info.Enabled);
        Assert.Empty(info.AppliedArguments);
    }

    [Fact]
    public void Every_profile_records_where_it_came_from_and_when_so_it_can_be_re_checked()
    {
        foreach (var profile in ModelFamilyProfile.All)
        {
            Assert.StartsWith("https://", profile.Source);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", profile.VerifiedOn);
            foreach (var recipe in profile.Recipes.Values)
            {
                Assert.All(recipe.Arguments, argument => Assert.DoesNotContain(' ', argument));
            }
        }
    }

    [Fact]
    public void The_generated_launch_arguments_include_the_profile_for_a_real_installation()
    {
        var record = new ManagedModelRecord
        {
            InstallationId = "custom-1", CatalogModelId = "custom", ProviderModelName = "meta-models/Muse-Glimmer-30B",
            Runtime = "vllm", Port = 18000, TensorParallelSize = 1, ExtraArguments = ["--max-num-batched-tokens", "8192"]
        };
        var options = new NodeAgentOptions(null, "docker", "vllm", "/tmp/d", "/tmp/c", "localhost", 18000, true, true, 60, 20,
            "llama", "sglang");

        var args = ManagedRuntimeProfiles.Arguments(record, options).ToList();

        AssertPair(args, "--tool-call-parser", "muse_glimmer");
        Assert.True(args.IndexOf("--tool-call-parser") < args.IndexOf("--max-num-batched-tokens"),
            "family flags come before the operator's, so the operator's later flags win");
        Assert.DoesNotContain("--tool-call-parser", ManagedRuntimeProfiles.Arguments(record, options with { ApplyModelFamilyProfiles = false }));
    }

    private static void AssertPair(IReadOnlyList<string> args, string flag, string value)
    {
        var index = args.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"{flag} missing");
        Assert.Equal(value, args[index + 1]);
    }
}
