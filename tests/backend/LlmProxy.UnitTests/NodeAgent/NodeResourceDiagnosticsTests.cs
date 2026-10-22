using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class NodeResourceDiagnosticsTests
{
    [Theory]
    [InlineData("25.5GiB / 120GiB", 25.5)]
    [InlineData("1024MiB / 12GiB", 1)]
    [InlineData("512KiB / 8GiB", 0.00048828125)]
    public void Docker_memory_is_parsed_with_binary_units(string text, double expected) =>
        Assert.Equal(expected, NodeResourceDiagnostics.ParseDockerMemoryGiB(text));

    [Fact]
    public void No_numeric_container_memory_is_not_misrepresented_as_zero() =>
        Assert.Null(NodeResourceDiagnostics.ParseDockerMemoryGiB("N/A / N/A"));

    [Fact]
    public void Vllm_log_reports_measured_historical_shortfall()
    {
        const string log = "ValueError: Free memory on device cuda:0 (45.65/121.63 GiB) on startup is less than desired GPU memory utilization (0.92, 111.9 GiB). Decrease GPU memory utilization";
        var diagnostic = NodeResourceDiagnostics.ParseRecentVllmShortfall(log, "lfm25-fp8-test", "LiquidAI/LFM2.5-24B-A2B-FP8");
        Assert.NotNull(diagnostic);
        Assert.Equal(45.65, diagnostic.FreeAtStartupGiB);
        Assert.Equal(111.9, diagnostic.RequestedAtStartupGiB);
        Assert.Equal(66.25, diagnostic.ShortfallAtStartupGiB);
    }

    [Fact]
    public void Compute_query_exposes_gpu_host_pid_and_memory()
    {
        var rows = NodeResourceDiagnostics.ParseComputeGpuProcesses(
            "3365, /home/agic/llama.cpp/build/bin/llama-server, 58119\n148918, /usr/lib/xorg/Xorg, N/A");
        Assert.Equal(2, rows.Count);
        Assert.Equal(3365, rows[0].Pid);
        Assert.Equal("llama-server", rows[0].ProcessName);
        Assert.Equal(58119d / 1024, rows[0].ReportedGpuMemoryGiB);
        Assert.Null(rows[1].ReportedGpuMemoryGiB);
    }

    [Fact]
    public void Nvidia_display_recovers_spark_gpu_memory_if_compute_query_is_incomplete()
    {
        const string output = """
|    0   N/A  N/A            3365      C   ...ma.cpp/build/bin/llama-server      58119MiB |
|    0   N/A  N/A          148918      G   /usr/lib/xorg/Xorg                      124MiB |
""";
        var rows = NodeResourceDiagnostics.ParseDisplayedGpuProcesses(output);
        Assert.Equal(2, rows.Count);
        Assert.Equal("llama-server", rows[0].ProcessName);
        Assert.Equal(58119d / 1024, rows[0].ReportedGpuMemoryGiB);
        Assert.Equal("G", rows[1].ProcessType);
    }

    [Fact]
    public void Unknown_log_does_not_claim_a_memory_shortfall() =>
        Assert.Null(NodeResourceDiagnostics.ParseRecentVllmShortfall(
            "CUDA initialization failed", "id", "model"));
}
