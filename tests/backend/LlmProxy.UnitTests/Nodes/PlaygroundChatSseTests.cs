using System.Text.Json;
using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Nodes;

public sealed class PlaygroundChatSseTests
{
    [Fact]
    public void Progress_is_an_explicit_waiting_event_not_a_fake_token_delta()
    {
        var raw = PlaygroundChatStreamEndpoints.FormatEvent("progress",
            new { phase = "waiting_for_model", secondsElapsed = 110 });
        Assert.StartsWith("event: progress\ndata: ", raw);
        Assert.EndsWith("\n\n", raw);
        Assert.DoesNotContain("choices", raw);
        Assert.DoesNotContain("delta", raw);
        var json = raw.Split("data: ")[1].Trim();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("waiting_for_model", doc.RootElement.GetProperty("phase").GetString());
        Assert.Equal(110, doc.RootElement.GetProperty("secondsElapsed").GetInt32());
    }

    [Fact]
    public void Result_preserves_status_and_escapes_content_to_a_single_sse_event()
    {
        var raw = PlaygroundChatStreamEndpoints.FormatEvent("result",
            new { statusCode = 200, success = true,
                responseBody = "{\"message\":\"line 1\\nline 2\"}" });
        Assert.StartsWith("event: result\ndata: ", raw);
        Assert.Equal(1, raw.Split("data: ").Length - 1);
        using var doc = JsonDocument.Parse(raw.Split("data: ")[1].Trim());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(200, doc.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Contains("\\n", doc.RootElement.GetProperty("responseBody").GetString());
    }
}
