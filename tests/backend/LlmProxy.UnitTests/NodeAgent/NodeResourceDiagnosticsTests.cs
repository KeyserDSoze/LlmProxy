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
    public void Unknown_log_does_not_claim_a_memory_shortfall() =>
        Assert.Null(NodeResourceDiagnostics.ParseRecentVllmShortfall(
            "CUDA initialization failed", "id", "model"));
}
