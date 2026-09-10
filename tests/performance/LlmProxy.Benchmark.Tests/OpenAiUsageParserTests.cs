using LlmProxy.Benchmarking;

namespace LlmProxy.Benchmark.Tests;

public sealed class OpenAiUsageParserTests
{
    [Fact]
    public void Parse_reads_chat_completion_usage()
    {
        var usage = OpenAiUsageParser.Parse("""{"usage":{"prompt_tokens":17,"completion_tokens":6,"total_tokens":23}}""");

        Assert.Equal(17, usage.InputTokens);
        Assert.Equal(6, usage.OutputTokens);
        Assert.Equal(23, usage.TotalTokens);
    }

    [Fact]
    public void Parse_reads_nested_responses_usage()
    {
        var usage = OpenAiUsageParser.Parse("""{"response":{"usage":{"input_tokens":11,"output_tokens":9,"total_tokens":20}}}""");

        Assert.Equal(11, usage.InputTokens);
        Assert.Equal(9, usage.OutputTokens);
        Assert.Equal(20, usage.TotalTokens);
    }

    [Fact]
    public void ContainsOutputDelta_ignores_role_only_chat_chunk_and_detects_content()
    {
        Assert.False(OpenAiUsageParser.ContainsOutputDelta("""{"choices":[{"delta":{"role":"assistant"}}]}""", BenchmarkSurface.ChatCompletions));
        Assert.True(OpenAiUsageParser.ContainsOutputDelta("""{"choices":[{"delta":{"content":"hello"}}]}""", BenchmarkSurface.ChatCompletions));
    }

    [Fact]
    public void ContainsOutputDelta_detects_responses_delta_event()
    {
        Assert.True(OpenAiUsageParser.ContainsOutputDelta("""{"type":"response.output_text.delta","delta":"hello"}""", BenchmarkSurface.Responses));
    }
}
