using System.Net;
using System.Text;
using LlmProxy.Benchmarking;

namespace LlmProxy.Benchmark.Tests;

public sealed class StreamingCompletionTests
{
    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n", true, null)]
    [InlineData("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n", false, "stream_incomplete")]
    public async Task Chat_stream_requires_terminal_marker(string payload, bool success, string? error)
    {
        var result = await RunAsync(BenchmarkSurface.ChatCompletions, payload);
        Assert.Equal(success ? 1 : 0, result.Levels[0].Succeeded);
        if (error is not null) Assert.Equal(1, result.Levels[0].ErrorBreakdown[error]);
    }

    [Theory]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"abc\"}\n\ndata: {\"type\":\"response.completed\"}\n\n", true)]
    [InlineData("data: {\"type\":\"response.failed\",\"error\":{\"message\":\"bad\"}}\n\ndata: [DONE]\n\n", false)]
    public async Task Responses_stream_accepts_completed_but_rejects_failed(string payload, bool success)
    {
        var result = await RunAsync(BenchmarkSurface.Responses, payload);
        Assert.Equal(success ? 1 : 0, result.Levels[0].Succeeded);
        if (!success) Assert.Equal(1, result.Levels[0].ErrorBreakdown["stream_failed"]);
    }

    private static async Task<BenchmarkReport> RunAsync(BenchmarkSurface surface, string payload)
    {
        using var client = new HttpClient(new StubHandler(payload));
        var runner = new BenchmarkRunner(client);
        return await runner.RunAsync(new BenchmarkOptions
        {
            Target = new Uri("http://localhost:8000"),
            Model = "test",
            Surface = surface,
            ConcurrencyLevels = [1],
            WarmupRequests = 0,
            RequestsPerLevel = 1,
            DelayBetweenLevels = TimeSpan.Zero
        });
    }

    private sealed class StubHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        }
    }
}
