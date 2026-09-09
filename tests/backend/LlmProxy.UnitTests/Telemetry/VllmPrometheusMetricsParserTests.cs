using LlmProxy.Infrastructure.Telemetry;

namespace LlmProxy.UnitTests.Telemetry;

public sealed class VllmPrometheusMetricsParserTests
{
    [Fact]
    public void Parse_reads_current_vllm_v1_metrics_and_sums_engine_values()
    {
        const string exposition = """
            # HELP vllm:num_requests_running Number of requests in model execution batches.
            vllm:num_requests_running{model_name="Qwen/Test",engine="0"} 2
            vllm:num_requests_running{model_name="Qwen/Test",engine="1"} 1
            vllm:num_requests_waiting{model_name="Qwen/Test"} 4
            vllm:kv_cache_usage_perc{model_name="Qwen/Test",engine="0"} 0.72
            vllm:kv_cache_usage_perc{model_name="Qwen/Test",engine="1"} 0.81
            vllm:prompt_tokens_total{model_name="Qwen/Test"} 1234
            vllm:generation_tokens_total{model_name="Qwen/Test"} 567
            """;

        var parsed = VllmPrometheusMetricsParser.TryParse(exposition, out var metrics);

        Assert.True(parsed);
        Assert.Equal("Qwen/Test", metrics.ModelName);
        Assert.Equal(3d, metrics.RunningRequests);
        Assert.Equal(4d, metrics.WaitingRequests);
        Assert.Equal(0.81d, metrics.KvCacheUsageRatio);
        Assert.Equal(1234d, metrics.PromptTokensTotal);
        Assert.Equal(567d, metrics.GenerationTokensTotal);
    }

    [Fact]
    public void Parse_supports_legacy_gpu_cache_metric_name()
    {
        const string exposition = """
            vllm:num_requests_running{model_name="legacy"} 0
            vllm:gpu_cache_usage_perc{model_name="legacy"} 0.45
            """;

        Assert.True(VllmPrometheusMetricsParser.TryParse(exposition, out var metrics));
        Assert.Equal(0.45d, metrics.KvCacheUsageRatio);
    }

    [Fact]
    public void Parse_returns_false_for_unrelated_prometheus_payload()
    {
        const string exposition = "process_cpu_seconds_total 12.4\n";

        Assert.False(VllmPrometheusMetricsParser.TryParse(exposition, out _));
    }
}
