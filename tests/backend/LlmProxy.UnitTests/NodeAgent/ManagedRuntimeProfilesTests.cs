using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class ManagedRuntimeProfilesTests
{
    [Fact]
    public void Vllm_profile_generates_explicit_runtime_flags()
    {
        var request = new InstallRequest("qwen3-4b", "Qwen/Qwen3-4B", null, 1, [], "vllm", 12, 8192, "fp8", 8);
        ManagedRuntimeProfiles.Validate(request);
        var args = ManagedRuntimeProfiles.Arguments(ToRecord(request), Options()).ToArray();
        Assert.Contains("--max-num-seqs", args);
        Assert.Contains("12", args);
        Assert.Contains("--kv-cache-dtype", args);
        Assert.Contains("fp8", args);
        Assert.Contains("--cpu-offload-gb", args);
        Assert.Contains("8", args);
    }

    [Fact]
    public void Llama_profile_uses_gguf_and_correct_port_and_parallel_flag()
    {
        var request = new InstallRequest("qwen3-4b-gguf-q4", "ggml-org/Qwen3-4B-GGUF:Q4_K_M", null, 1, [], "llama.cpp", 4, 32768);
        ManagedRuntimeProfiles.Validate(request);
        var args = ManagedRuntimeProfiles.Arguments(ToRecord(request), Options()).ToArray();
        Assert.Contains("--hf-repo", args);
        Assert.Contains("--parallel", args);
        Assert.Contains("4", args);
        Assert.Contains("--ctx-size", args);
        Assert.Contains("32768", args);
        Assert.DoesNotContain("--tensor-parallel-size", args);
    }

    [Fact]
    public void Sglang_profile_uses_server_compatible_flags()
    {
        var request = new InstallRequest("awq-sglang", "Qwen/Qwen3-4B-AWQ", null, 1, [], "sglang", 12, 8192);
        ManagedRuntimeProfiles.Validate(request);
        var args = ManagedRuntimeProfiles.Arguments(ToRecord(request), Options()).ToArray();
        Assert.Contains("sglang.launch_server", args);
        Assert.Contains("--max-running-requests", args);
        Assert.Contains("--context-length", args);
        Assert.Contains("--enable-metrics", args);
        Assert.DoesNotContain("--max-num-seqs", args);
    }

    [Theory]
    [InlineData("airllm", "ggml-org/Qwen3-4B-GGUF:Q4_K_M", null)]
    [InlineData("llama.cpp", "Qwen/Qwen3-4B", "fp8")]
    [InlineData("sglang", "Qwen/Qwen3-4B-AWQ", "fp8")]
    public void Invalid_profiles_are_rejected(string runtime, string model, string? kv)
    {
        var request = new InstallRequest("test", model, null, 1, [], runtime, 4, 8192, kv);
        Assert.ThrowsAny<ArgumentException>(() => ManagedRuntimeProfiles.Validate(request));
    }

    [Fact]
    public void Same_model_different_profile_cannot_be_silently_reused()
    {
        var baseline = new InstallRequest("qwen3-4b", "Qwen/Qwen3-4B", null, 1, [], "vllm", 1, 8192);
        var changed = baseline with { MaxNumSeqs = 12 };
        Assert.False(ManagedRuntimeProfiles.Matches(ToRecord(baseline), changed));
        Assert.True(ManagedRuntimeProfiles.Matches(ToRecord(baseline), baseline));
    }

    private static ManagedModelRecord ToRecord(InstallRequest r) => new()
    {
        InstallationId = "test",
        CatalogModelId = r.CatalogModelId,
        ProviderModelName = r.ProviderModelName,
        Runtime = r.Runtime,
        MaxNumSeqs = r.MaxNumSeqs,
        MaxModelLen = r.MaxModelLen,
        KvCacheDtype = r.KvCacheDtype,
        CpuOffloadGiB = r.CpuOffloadGiB,
        Port = 18000,
        TensorParallelSize = r.TensorParallelSize,
        ExtraArguments = r.ExtraArguments ?? []
    };

    private static NodeAgentOptions Options() =>
        new(null, "docker", "vllm/vllm-openai:latest", "/tmp/state", "/tmp/models",
            "localhost", 18000, true, true, 60, 20, "ghcr.io/ggml-org/llama.cpp:server-cuda", "lmsysorg/sglang:latest");
}
