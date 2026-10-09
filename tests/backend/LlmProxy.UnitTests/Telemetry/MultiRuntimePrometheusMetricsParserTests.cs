using LlmProxy.Infrastructure.Telemetry;

namespace LlmProxy.UnitTests.Telemetry;

public sealed class MultiRuntimePrometheusMetricsParserTests
{
    [Fact]
    public void Sglang_metrics_are_normalized_with_per_model_queue_and_cache()
    {
        const string body = """
sglang:num_running_reqs{model_name="Qwen/Test"} 3
sglang:num_queue_reqs{model_name="Qwen/Test"} 4
sglang:token_usage{model_name="Qwen/Test"} 0.65
""";
        Assert.True(MultiRuntimePrometheusMetricsParser.TryParse(body, out var m));
        Assert.Equal("sglang", m.Runtime);
        Assert.Equal("Qwen/Test", m.ModelName);
        Assert.Equal(3d, m.Running);
        Assert.Equal(4d, m.Waiting);
        Assert.Equal(0.65d, m.CacheUsage);
        Assert.Null(m.GenerationTokens);
    }

    [Fact]
    public void Llama_cpp_metrics_are_normalized_without_inventing_missing_values()
    {
        const string body = """
llamacpp:requests_processing 2
llamacpp:requests_deferred 1
llamacpp:kv_cache_usage_ratio 0.2
llamacpp:prompt_tokens_total 100
llamacpp:tokens_predicted_total 34
""";
        Assert.True(MultiRuntimePrometheusMetricsParser.TryParse(body, out var m));
        Assert.Equal("llama.cpp", m.Runtime);
        Assert.Equal(2d, m.Running);
        Assert.Equal(1d, m.Waiting);
        Assert.Equal(0.2d, m.CacheUsage);
        Assert.Equal(100d, m.PromptTokens);
        Assert.Equal(34d, m.GenerationTokens);
    }

    [Fact]
    public void Vllm_remains_compatible()
    {
        Assert.True(MultiRuntimePrometheusMetricsParser.TryParse(
            "vllm:num_requests_running{model_name=\"Qwen/Test\"} 1\n", out var metrics));
        Assert.Equal("vllm", metrics.Runtime);
    }

    [Fact]
    public void Unrelated_or_invalid_prometheus_data_is_not_treated_as_available()
    {
        Assert.False(MultiRuntimePrometheusMetricsParser.TryParse("process_cpu_seconds_total 1\n", out _));
        Assert.False(MultiRuntimePrometheusMetricsParser.TryParse("sglang:num_running_reqs NaN\n", out _));
    }
}
