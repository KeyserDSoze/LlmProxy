using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Api;

public sealed class ModelCapabilityProbeTests
{
    private static string Completion(string content, string? finish = "stop", string? reasoningField = null, string? reasoning = null, string? toolCalls = null) =>
        "{\"choices\":[{\"finish_reason\":" + (finish is null ? "null" : $"\"{finish}\"") +
        ",\"message\":{\"role\":\"assistant\",\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) +
        (reasoningField is null ? "" : $",\"{reasoningField}\":{System.Text.Json.JsonSerializer.Serialize(reasoning)}") +
        (toolCalls is null ? "" : $",\"tool_calls\":{toolCalls}") + "}}]}";

    [Fact]
    public void Chat_is_ok_when_the_model_answers()
        => Assert.Equal("ok", ModelCapabilityProbe.Chat(200, Completion("ok")).Status);

    [Fact]
    public void Chat_warns_when_reasoning_used_the_whole_budget_but_does_not_call_serving_broken()
    {
        var check = ModelCapabilityProbe.Chat(200, Completion("", "length", "reasoning", "thinking about it"));

        Assert.Equal("warning", check.Status);
        Assert.Contains("max_tokens", check.Detail);
    }

    [Fact]
    public void Chat_fails_with_the_server_message_on_an_http_error()
    {
        var check = ModelCapabilityProbe.Chat(500, "{\"error\":{\"message\":\"model is loading\"}}");

        Assert.Equal("failed", check.Status);
        Assert.Equal("HTTP 500: model is loading", check.Detail);
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("reasoning_content")]
    public void Reasoning_is_ok_with_either_field_name_vllm_renamed_it_from_reasoning_content_to_reasoning(string field)
        => Assert.Equal("ok", ModelCapabilityProbe.Reasoning(200, Completion("ok", reasoningField: field, reasoning: "step by step")).Status);

    [Theory]
    [InlineData("<think>let me think</think>ok")]
    [InlineData("ok</think>")]
    public void Reasoning_fails_when_the_thinking_markers_leak_into_the_answer(string content)
    {
        var check = ModelCapabilityProbe.Reasoning(200, Completion(content));

        Assert.Equal("failed", check.Status);
        Assert.Contains("parser", check.Detail);
    }

    [Fact]
    public void Reasoning_is_inconclusive_for_a_plain_answer_because_not_thinking_proves_nothing()
        => Assert.Equal("inconclusive", ModelCapabilityProbe.Reasoning(200, Completion("ok")).Status);

    [Fact]
    public void Tools_are_ok_when_tool_calls_carry_the_expected_function_and_json_arguments()
    {
        var calls = "[{\"id\":\"1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"Paris\\\"}\"}}]";

        Assert.Equal("ok", ModelCapabilityProbe.Tools(200, Completion("", "tool_calls", toolCalls: calls)).Status);
    }

    [Fact]
    public void Tools_warn_when_the_arguments_are_not_a_json_object()
    {
        var calls = "[{\"function\":{\"name\":\"get_weather\",\"arguments\":\"Paris\"}}]";

        Assert.Equal("warning", ModelCapabilityProbe.Tools(200, Completion("", "tool_calls", toolCalls: calls)).Status);
    }

    [Theory]
    [InlineData("<tool_call>{\"name\":\"get_weather\"}</tool_call>")]
    [InlineData("<atem:function_calls><atem:invoke name=\"get_weather\">")]
    [InlineData("<function=get_weather>{}")]
    public void Tools_fail_when_the_model_writes_tool_syntax_as_text_which_means_the_parser_is_missing(string content)
    {
        var check = ModelCapabilityProbe.Tools(200, Completion(content));

        Assert.Equal("failed", check.Status);
        Assert.Contains("parser", check.Detail);
    }

    [Fact]
    public void Tools_are_inconclusive_when_the_model_simply_answers_in_words()
        => Assert.Equal("inconclusive", ModelCapabilityProbe.Tools(200, Completion("It is sunny in Paris.")).Status);

    [Fact]
    public void Tools_fail_clearly_when_vllm_was_started_without_auto_tool_choice()
    {
        var body = "{\"error\":{\"message\":\"\\\"auto\\\" tool choice requires --enable-auto-tool-choice and --tool-call-parser to be set\"}}";

        var check = ModelCapabilityProbe.Tools(400, body);

        Assert.Equal("failed", check.Status);
        Assert.Contains("--enable-auto-tool-choice", check.Detail);
    }

    [Fact]
    public void Streaming_requires_real_sse_ended_by_done()
    {
        Assert.Equal("ok", ModelCapabilityProbe.Streaming(200, "text/event-stream; charset=utf-8", "data: {}\n\ndata: [DONE]\n\n").Status);
        Assert.Equal("warning", ModelCapabilityProbe.Streaming(200, "text/event-stream", "data: {}\n\n").Status);
        Assert.Equal("failed", ModelCapabilityProbe.Streaming(200, "application/json", "{}").Status);
        Assert.Equal("failed", ModelCapabilityProbe.Streaming(200, "text/event-stream", "").Status);
    }

    [Fact]
    public void Probe_requests_are_tiny_and_use_the_provider_model_name_the_gateway_would_send()
    {
        var chat = ModelCapabilityProbe.ChatRequest("meta-models/Muse-Glimmer-30B");
        var tools = ModelCapabilityProbe.ToolRequest("meta-models/Muse-Glimmer-30B");

        Assert.Equal("meta-models/Muse-Glimmer-30B", chat["model"]!.GetValue<string>());
        Assert.False(chat["stream"]!.GetValue<bool>());
        Assert.True(chat["max_tokens"]!.GetValue<int>() <= 256);
        Assert.Equal("auto", tools["tool_choice"]!.GetValue<string>());
        Assert.True(ModelCapabilityProbe.ChatRequest("m", stream: true)["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void Error_details_are_bounded_so_a_runtime_cannot_flood_the_admin_screen()
    {
        var check = ModelCapabilityProbe.Chat(500, new string('x', 5000));

        Assert.True(check.Detail.Length < 260);
    }
}
