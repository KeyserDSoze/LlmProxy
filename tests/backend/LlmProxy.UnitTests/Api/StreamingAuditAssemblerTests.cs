using System.Text;
using System.Text.Json;
using LlmProxy.Api.Observability;
using LlmProxy.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace LlmProxy.UnitTests.Api;

public sealed class StreamingAuditAssemblerTests
{
    [Fact]
    public void Reassembles_chat_text_utf8_metadata_and_tool_arguments_across_arbitrary_writes()
    {
        var assembler = new StreamingAuditAssembler("chat_completions");
        var sse = """
            data: {"id":"chat-123","model":"provider","choices":[{"index":0,"delta":{"role":"assistant","content":"Caf"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"content":"é ☕","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"search","arguments":"{\"q\":\""}}]},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"test\"}"}}]},"finish_reason":"tool_calls"}]}

            data: {"usage":{"prompt_tokens":7,"completion_tokens":4,"total_tokens":11},"choices":[]}

            data: [DONE]

            """.Replace("\n", "\r\n");
        var bytes = Encoding.UTF8.GetBytes(sse);
        for (var index = 0; index < bytes.Length; index++)
            assembler.Append(bytes.AsSpan(index, 1));

        using var result = JsonDocument.Parse(assembler.Build());
        var root = result.RootElement;
        Assert.Equal("completed", root.GetProperty("state").GetString());
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal(5, root.GetProperty("eventsProcessed").GetInt32());
        var response = root.GetProperty("response");
        Assert.Equal("chat-123", response.GetProperty("id").GetString());
        Assert.Equal(11, response.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        var choice = response.GetProperty("choices")[0];
        Assert.Equal("Café ☕", choice.GetProperty("message").GetProperty("content").GetString());
        var tool = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("search", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"q\":\"test\"}", tool.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void Preserves_partial_chat_on_client_cancellation_even_with_http_200()
    {
        var assembler = new StreamingAuditAssembler("chat_completions");
        assembler.Append(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Partially delivered\"}}]}\n\n"));
        using var result = JsonDocument.Parse(assembler.Build(cancelled: true));
        var root = result.RootElement;
        Assert.Equal("cancelled", root.GetProperty("state").GetString());
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.Equal("client_cancelled", root.GetProperty("reason").GetString());
        Assert.Equal("Partially delivered", root.GetProperty("response")
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Identifies_upstream_interruption_and_never_pretends_the_stream_completed()
    {
        var assembler = new StreamingAuditAssembler("chat_completions");
        assembler.Append(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"}}]}\n\n"));
        using var result = JsonDocument.Parse(assembler.Build(cancelled: true, upstreamFailure: "upstream_stream_interrupted"));
        Assert.Equal("interrupted", result.RootElement.GetProperty("state").GetString());
        Assert.Equal("upstream_stream_interrupted", result.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Reconstructs_responses_partial_output_and_completed_snapshot()
    {
        var assembler = new StreamingAuditAssembler("responses");
        assembler.Append(Encoding.UTF8.GetBytes(
            "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello \"}\n\n" +
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"world\"}\n\n"));
        using (var partial = JsonDocument.Parse(assembler.Build()))
        {
            Assert.Equal("incomplete", partial.RootElement.GetProperty("state").GetString());
            Assert.Equal("Hello world", partial.RootElement.GetProperty("response").GetProperty("output")[0]
                .GetProperty("content")[0].GetProperty("text").GetString());
        }

        assembler.Append(Encoding.UTF8.GetBytes(
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"object\":\"response\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello world\"}]}],\"usage\":{\"output_tokens\":2}}}\n\n"));
        using var final = JsonDocument.Parse(assembler.Build());
        Assert.Equal("completed", final.RootElement.GetProperty("state").GetString());
        Assert.Equal("resp-1", final.RootElement.GetProperty("response").GetProperty("id").GetString());
        Assert.Equal(2, final.RootElement.GetProperty("response").GetProperty("usage")
            .GetProperty("output_tokens").GetInt32());
    }


    [Fact]
    public void Bounds_responses_final_text_without_delta_events()
    {
        var assembler = new StreamingAuditAssembler("responses", maxCapturedCharacters: 5);
        assembler.Append(Encoding.UTF8.GetBytes(
            "event: response.output_text.done\\ndata: {\\"type\\":\\"response.output_text.done\\",\\"output_index\\":0,\\"content_index\\":0,\\"text\\":\\"unbounded\\"}\\n\\n"));
        using var result = JsonDocument.Parse(assembler.Build());
        Assert.True(result.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Bounds_text_while_preserving_a_valid_partial_audit_json()
    {
        var assembler = new StreamingAuditAssembler("chat_completions", maxCapturedCharacters: 3);
        assembler.Append(Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"abcdef\"}}]}\n\n"));
        using var result = JsonDocument.Parse(assembler.Build());
        Assert.True(result.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("abc", result.RootElement.GetProperty("response").GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString());
    }
}

public sealed class InferenceContentLoggingMiddlewareTests
{
    [Fact]
    public async Task Forwards_sse_unchanged_but_persists_only_reconstructed_response()
    {
        const string first = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"}}]}\n\n";
        const string second = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"}}]}\n\ndata: [DONE]\n\n";
        var sink = new CapturingSink();
        await using var downstream = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"model\":\"test\",\"stream\":true}"));
        context.Response.Body = downstream;

        var middleware = new InferenceContentLoggingMiddleware(async http =>
        {
            http.Response.ContentType = "text/event-stream";
            await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(first));
            await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(second));
        });
        await middleware.InvokeAsync(context, sink);

        Assert.Equal(first + second, Encoding.UTF8.GetString(downstream.ToArray()));
        Assert.NotNull(sink.Record);
        Assert.DoesNotContain("data:", sink.Record.ResponseBody);
        using var result = JsonDocument.Parse(sink.Record.ResponseBody);
        Assert.Equal("completed", result.RootElement.GetProperty("state").GetString());
        Assert.Equal("Hello world", result.RootElement.GetProperty("response").GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("{\"model\":\"test\",\"stream\":true}", sink.Record.RequestBody);
    }

    private sealed class CapturingSink : IInferenceContentLogSink
    {
        public InferenceContentLog? Record { get; private set; }

        public void Write(InferenceContentLog log) => Record = log;
    }
}
