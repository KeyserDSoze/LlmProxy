using LlmProxy.Api.OpenAi;

namespace LlmProxy.UnitTests.OpenAi;

public sealed class OpenAiRequestPayloadTests
{
    [Fact]
    public void Rejects_invalid_json()
    {
        var result = OpenAiRequestPayload.TryParse(
            "{",
            out _,
            out _,
            out var errorCode,
            out var errorMessage);

        Assert.False(result);
        Assert.Equal("invalid_json", errorCode);
        Assert.Equal("Request body is not valid JSON.", errorMessage);
    }

    [Fact]
    public void Rejects_payload_without_a_string_model()
    {
        var result = OpenAiRequestPayload.TryParse(
            "{\"model\":42,\"input\":\"hello\"}",
            out _,
            out _,
            out var errorCode,
            out var errorMessage);

        Assert.False(result);
        Assert.Equal("model_required", errorCode);
        Assert.Equal("A logical model name is required.", errorMessage);
    }

    [Fact]
    public void Rewrites_only_the_model_and_preserves_responses_api_fields()
    {
        var rawBody = """
        {
          "model": "agic-code-fast",
          "input": [
            {
              "role": "user",
              "content": [
                { "type": "input_text", "text": "Review this code" }
              ]
            }
          ],
          "tools": [
            {
              "type": "function",
              "name": "lookup_symbol",
              "description": "Look up a symbol",
              "parameters": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" }
                }
              }
            }
          ],
          "stream": true,
          "metadata": {
            "source": "copilot"
          }
        }
        """;

        var result = OpenAiRequestPayload.TryParse(
            rawBody,
            out var payload,
            out var logicalModel,
            out _,
            out _);

        Assert.True(result);
        Assert.Equal("agic-code-fast", logicalModel);

        OpenAiRequestPayload.RewriteModel(payload, "Qwen/Qwen3-Coder");

        Assert.Equal("Qwen/Qwen3-Coder", payload["model"]!.GetValue<string>());
        Assert.True(payload["stream"]!.GetValue<bool>());
        Assert.Equal("copilot", payload["metadata"]!["source"]!.GetValue<string>());
        Assert.Equal("function", payload["tools"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("lookup_symbol", payload["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("input_text", payload["input"]![0]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Review this code", payload["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Preserves_chat_completion_tool_calling_fields()
    {
        var rawBody = """
        {
          "model": "agic-code-fast",
          "messages": [
            { "role": "user", "content": "Find symbol Foo" }
          ],
          "tools": [
            {
              "type": "function",
              "function": {
                "name": "find_symbol",
                "parameters": {
                  "type": "object",
                  "properties": {
                    "symbol": { "type": "string" }
                  }
                }
              }
            }
          ],
          "tool_choice": "auto"
        }
        """;

        var result = OpenAiRequestPayload.TryParse(
            rawBody,
            out var payload,
            out var logicalModel,
            out _,
            out _);

        Assert.True(result);
        Assert.Equal("agic-code-fast", logicalModel);

        OpenAiRequestPayload.RewriteModel(payload, "provider-model");

        Assert.Equal("provider-model", payload["model"]!.GetValue<string>());
        Assert.Equal("auto", payload["tool_choice"]!.GetValue<string>());
        Assert.Equal("find_symbol", payload["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Foo", payload["messages"]![0]!["content"]!.GetValue<string>().Split(' ').Last());
    }
}
