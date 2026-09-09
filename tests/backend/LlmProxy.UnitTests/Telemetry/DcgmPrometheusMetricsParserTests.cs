using LlmProxy.Infrastructure.Telemetry;

namespace LlmProxy.UnitTests.Telemetry;

public sealed class DcgmPrometheusMetricsParserTests
{
    [Fact]
    public void Parse_aggregates_multi_gpu_dcgm_metrics()
    {
        const string exposition = """
            # HELP DCGM_FI_DEV_GPU_UTIL GPU utilization (in %).
            DCGM_FI_DEV_GPU_UTIL{gpu="0",UUID="GPU-a"} 40
            DCGM_FI_DEV_GPU_UTIL{gpu="1",UUID="GPU-b"} 80
            DCGM_FI_DEV_FB_USED{gpu="0",UUID="GPU-a"} 1000
            DCGM_FI_DEV_FB_USED{gpu="1",UUID="GPU-b"} 3000
            DCGM_FI_DEV_FB_FREE{gpu="0",UUID="GPU-a"} 7000
            DCGM_FI_DEV_FB_FREE{gpu="1",UUID="GPU-b"} 5000
            DCGM_FI_DEV_GPU_TEMP{gpu="0",UUID="GPU-a"} 61
            DCGM_FI_DEV_GPU_TEMP{gpu="1",UUID="GPU-b"} 67
            DCGM_FI_DEV_POWER_USAGE{gpu="0",UUID="GPU-a"} 120.5
            DCGM_FI_DEV_POWER_USAGE{gpu="1",UUID="GPU-b"} 140.5
            """;

        var parsed = DcgmPrometheusMetricsParser.TryParse(exposition, out var metrics);

        Assert.True(parsed);
        Assert.Equal(2, metrics.GpuCount);
        Assert.Equal(60d, metrics.AverageGpuUtilizationPercent);
        Assert.Equal(80d, metrics.MaxGpuUtilizationPercent);
        Assert.Equal(4000d, metrics.FramebufferUsedMiB);
        Assert.Equal(12000d, metrics.FramebufferFreeMiB);
        Assert.Equal(0.25d, metrics.FramebufferUsageRatio);
        Assert.Equal(67d, metrics.MaxTemperatureCelsius);
        Assert.Equal(261d, metrics.TotalPowerUsageWatts);
    }

    [Fact]
    public void Parse_prefers_reported_framebuffer_total_for_usage_ratio()
    {
        const string exposition = """
            DCGM_FI_DEV_FB_USED{gpu="0"} 3000
            DCGM_FI_DEV_FB_FREE{gpu="0"} 4000
            DCGM_FI_DEV_FB_TOTAL{gpu="0"} 8000
            """;

        Assert.True(DcgmPrometheusMetricsParser.TryParse(exposition, out var metrics));
        Assert.Equal(0.375d, metrics.FramebufferUsageRatio);
    }

    [Fact]
    public void Parse_returns_false_for_unrelated_prometheus_payload()
    {
        const string exposition = "process_cpu_seconds_total 12.4\n";

        Assert.False(DcgmPrometheusMetricsParser.TryParse(exposition, out _));
    }
}
