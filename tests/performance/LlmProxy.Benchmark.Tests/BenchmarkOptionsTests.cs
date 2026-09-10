using LlmProxy.Benchmarking;

namespace LlmProxy.Benchmark.Tests;

public sealed class BenchmarkOptionsTests
{
    [Fact]
    public void Parse_supports_path_prefixed_service_root_and_concurrency_sweep()
    {
        var options = BenchmarkOptions.Parse(new[]
        {
            "--target", "http://localhost:8080/agic-proxy",
            "--model", "agic-code",
            "--concurrency", "1,4,8",
            "--requests", "20",
            "--api-key-env", "LLMPROXY_API_KEY"
        });

        Assert.Equal("http://localhost:8080/agic-proxy", options.Target.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("agic-code", options.Model);
        Assert.Equal(new[] { 1, 4, 8 }, options.ConcurrencyLevels);
        Assert.Equal(20, options.RequestsPerLevel);
        Assert.Equal("LLMPROXY_API_KEY", options.ApiKeyEnvironmentVariable);
        Assert.True(options.Streaming);
    }

    [Fact]
    public void Parse_rejects_target_with_embedded_credentials()
    {
        var exception = Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(new[]
        {
            "--target", "http://user:secret@localhost:8080",
            "--model", "agic-code"
        }));

        Assert.Contains("embedded credentials", exception.Message);
    }

    [Fact]
    public void Endpoint_builder_accepts_roots_with_or_without_v1()
    {
        Assert.Equal(
            "http://localhost:8080/prefix/v1/chat/completions",
            BenchmarkEndpointBuilder.Build(new Uri("http://localhost:8080/prefix"), BenchmarkSurface.ChatCompletions).AbsoluteUri);
        Assert.Equal(
            "http://localhost:8080/prefix/v1/responses",
            BenchmarkEndpointBuilder.Build(new Uri("http://localhost:8080/prefix/v1"), BenchmarkSurface.Responses).AbsoluteUri);
    }
}
