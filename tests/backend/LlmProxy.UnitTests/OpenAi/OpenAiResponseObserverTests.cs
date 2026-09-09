using System.Text;
using LlmProxy.Api.OpenAi;

namespace LlmProxy.UnitTests.OpenAi;

public sealed class OpenAiResponseObserverTests
{
    [Fact]
    public void Non_streaming_chat_usage_and_first_byte_are_observed()
    {
        var elapsed = 125L;
        var observer = new OpenAiResponseObserver(streaming: false, () => elapsed);
        var payload = Encoding.UTF8.GetBytes("""
            {"id":"chatcmpl-1","usage":{"prompt_tokens":11,"completion_tokens":7,"total_tokens":18}}
            """);

        observer.Observe(payload.AsSpan(0, 12));
        elapsed = 250;
        observer.Observe(payload.AsSpan(12));
        observer.Complete();

        Assert.Equal(125, observer.TimeToFirstByteMilliseconds);
        Assert.NotNull(observer.Usage);
        Assert.Equal(11, observer.Usage.InputTokens);
        Assert.Equal(7, observer.Usage.OutputTokens);
        Assert.Equal(18, observer.Usage.TotalTokens);
    }

    [Fact]
    public void Streaming_usage_is_parsed_when_an_sse_line_is_split_across_chunks()
    {
        var elapsed = 40L;
        var observer = new OpenAiResponseObserver(streaming: true, () => elapsed);
        var stream = Encoding.UTF8.GetBytes(
            "data: {\"id\":\"chunk-1\",\"choices\":[]}\n\n" +
            "data: {\"id\":\"chunk-1\",\"usage\":{\"prompt_tokens\":13,\"completion_tokens\":5,\"total_tokens\":18}}\n\n" +
            "data: [DONE]\n\n");

        observer.Observe(stream.AsSpan(0, 57));
        elapsed = 110;
        observer.Observe(stream.AsSpan(57));
        observer.Complete();

        Assert.True(observer.IsStreaming);
        Assert.Equal(40, observer.TimeToFirstByteMilliseconds);
        Assert.NotNull(observer.Usage);
        Assert.Equal(13, observer.Usage.InputTokens);
        Assert.Equal(5, observer.Usage.OutputTokens);
        Assert.Equal(18, observer.Usage.TotalTokens);
    }

    [Fact]
    public void Responses_usage_can_be_nested_in_a_completed_event()
    {
        var observer = new OpenAiResponseObserver(streaming: true, () => 15L);
        var stream = Encoding.UTF8.GetBytes(
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":21,\"output_tokens\":9,\"total_tokens\":30}}}\n\n");

        observer.Observe(stream);
        observer.Complete();

        Assert.NotNull(observer.Usage);
        Assert.Equal(21, observer.Usage.InputTokens);
        Assert.Equal(9, observer.Usage.OutputTokens);
        Assert.Equal(30, observer.Usage.TotalTokens);
    }

    [Fact]
    public void Invalid_usage_payload_is_ignored_without_affecting_proxy_observation()
    {
        var observer = new OpenAiResponseObserver(streaming: false, () => 5L);
        observer.Observe(Encoding.UTF8.GetBytes("not-json"));
        observer.Complete();

        Assert.Equal(5, observer.TimeToFirstByteMilliseconds);
        Assert.Null(observer.Usage);
    }
}
