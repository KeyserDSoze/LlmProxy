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
}
