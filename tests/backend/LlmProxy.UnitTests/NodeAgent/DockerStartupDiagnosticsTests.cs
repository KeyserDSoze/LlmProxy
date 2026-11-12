using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class DockerStartupDiagnosticsTests
{
    [Fact]
    public void Root_cause_from_vllm_worker_precedes_engine_core_traceback()
    {
        var log = """
            (EngineCore pid=100) ERROR [core.py] ValueError: Free memory on device cuda:0 (45.65/121.63 GiB) on startup is less than desired GPU memory utilization (0.92, 111.9 GiB).
            (APIServer pid=1) Traceback (most recent call last):
            File "/usr/local/lib/python3.12/dist-packages/vllm/v1/engine/utils.py", line 1431, in wait_for_engine_startup
            RuntimeError: Engine core initialization failed. See root cause above. Failed core proc(s): {}
            """;
        Assert.Contains("ValueError: Free memory on device", DockerStartupDiagnostics.FindRootCause(log));
        var detail = DockerStartupDiagnostics.ForUi(log, "/var/lib/llmproxy-node-agent/startup-logs/test.log");
        Assert.Contains("Likely root cause: ValueError:", detail);
        Assert.Contains("45.65/121.63 GiB", detail);
        Assert.Contains("startup-logs/test.log", detail);
    }

    [Fact]
    public void CUDA_out_of_memory_wins_over_generic_engine_exception()
    {
        var log = """
            RuntimeError: Worker process failed.
            (EngineCore pid=100) torch.cuda.OutOfMemoryError: CUDA out of memory. Tried to allocate 24.00 GiB.
            RuntimeError: Engine core initialization failed. See root cause above.
            """;
        Assert.Contains("OutOfMemoryError:", DockerStartupDiagnostics.FindRootCause(log));
    }

    [Fact]
    public void Missing_root_is_not_invented()
    {
        Assert.Null(DockerStartupDiagnostics.FindRootCause(
            "RuntimeError: Engine core initialization failed. See root cause above."));
        Assert.Contains("did not contain an identifiable root exception",
            DockerStartupDiagnostics.ForUi(
                "RuntimeError: Engine core initialization failed. See root cause above.", null));
    }

    [Fact]
    public void Tokens_are_redacted_from_diagnostic_output()
    {
        const string hf = "hf_abcdefgh12345678901234";
        var log = "Authorization Bearer abcdefghijklmnopqrstuvwxyz\n" +
            "HF_TOKEN=" + hf + "\nValueError: cannot load " + hf;
        var sanitized = DockerStartupDiagnostics.Sanitize(log);
        Assert.DoesNotContain(hf, sanitized);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", sanitized);
        Assert.Contains("ValueError", sanitized);
    }

    // The exact tail reported from a DGX host when -hf could not resolve a GGUF.
    private const string EmptyModelTail = """
        0.00.750.554 E llama_model_load_from_file_impl: exactly one out metadata, path_model, and file must be defined
        0.00.750.571 E common_fit_params: encountered an error while trying to fit params to free device memory: failed to load model
        0.00.750.578 E cmn common_init_: failed to load model ''
        0.00.750.580 E srv load_model: failed to load model, ''
        0.00.751.477 E srv server_run_i: exiting due to model loading error
        """;

    [Fact]
    public void Llama_cpp_empty_model_path_is_explained_instead_of_unidentified()
    {
        var detail = DockerStartupDiagnostics.ForUi(EmptyModelTail, null);
        Assert.Contains("Likely root cause:", detail);
        Assert.Contains("exactly one out metadata", detail);
        Assert.Contains("What to do: llama.cpp started without a resolved GGUF file", detail);
        Assert.DoesNotContain("did not contain an identifiable root exception", detail);
    }

    [Fact]
    public void Earlier_llama_cpp_hub_error_wins_over_the_empty_model_symptom()
    {
        var log = "common_download_get_hf_plan: no GGUF files found in repository org/model\n" +
                  "Available GGUF files:\n" + EmptyModelTail;
        Assert.Contains("no GGUF files found in repository org/model", DockerStartupDiagnostics.FindRootCause(log));
        Assert.Contains("Pick an available quantization", DockerStartupDiagnostics.ForUi(log, null));

        var gated = "get_repo_commit: error: GET failed (401): Invalid credentials\n" + EmptyModelTail;
        Assert.Contains("GET failed (401)", DockerStartupDiagnostics.FindRootCause(gated));
    }

    [Fact]
    public void Unsupported_llama_cpp_architecture_suggests_a_newer_image()
    {
        var log = "llama_model_load: error loading model: error loading model architecture: unknown model architecture: 'lfm2moe'\n" +
                  "llama_model_load_from_file_impl: failed to load model\n";
        Assert.Contains("unknown model architecture: 'lfm2moe'", DockerStartupDiagnostics.FindRootCause(log));
        Assert.Contains("LlamaCppDockerImage", DockerStartupDiagnostics.ForUi(log, null));
    }
}
