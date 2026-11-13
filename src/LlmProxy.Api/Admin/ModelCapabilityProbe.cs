using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmProxy.Api.Admin;

/// <summary>One verified (or refuted) capability of a running model.</summary>
public sealed record ProbeCheck(string Id, string Status, string Detail);

/// <summary>
/// Interprets the answers of a running model to a few tiny requests. Documentation says what a model family
/// should do; these checks say what this running server actually does: whether the tool-call parser turns
/// tool syntax into <c>tool_calls</c>, whether reasoning is split from the answer, whether streaming is real SSE.
/// Statuses: ok, warning, failed, inconclusive (the request worked but proved nothing either way).
/// </summary>
public static class ModelCapabilityProbe
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Failed = "failed";
    public const string Inconclusive = "inconclusive";

    private static readonly string[] ToolSyntaxInText =
        ["<tool_call>", "<function=", "<atem:function_calls>", "<|python_tag|>", "[TOOL_CALLS]", "<|tool_call"];

    private static readonly string[] ReasoningInText = ["<think>", "</think>", "<|channel|>analysis", "<|start|>assistant to=self"];

    public static JsonObject ChatRequest(string model, bool stream = false, int maxTokens = 192) => new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "Reply with the single word: ok"
        }),
        ["max_tokens"] = maxTokens,
        ["stream"] = stream
    };

    public static JsonObject ToolRequest(string model) => new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "What is the weather in Paris in celsius? Use the get_weather tool."
        }),
        ["tools"] = new JsonArray(new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "get_weather",
                ["description"] = "Get current weather for a city.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["city"] = new JsonObject { ["type"] = "string" },
                        ["units"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("celsius", "fahrenheit") }
                    },
                    ["required"] = new JsonArray("city")
                }
            }
        }),
        ["tool_choice"] = "auto",
        ["max_tokens"] = 768
    };

    public static ProbeCheck Chat(int status, string body)
    {
        if (status != 200) return new("chat", Failed, HttpFailure(status, body));
        var message = FirstMessage(body, out var finish);
        if (message is null) return new("chat", Failed, "HTTP 200 but the answer has no choices[0].message.");
        var content = TextOf(message["content"]);
        var reasoning = ReasoningOf(message);
        if (content.Length == 0 && reasoning.Length > 0 && finish == "length")
            return new("chat", Warning,
                "Answered, but the whole output budget went into reasoning (finish_reason=length). Serving works; give clients a larger max_tokens.");
        if (content.Length == 0 && reasoning.Length == 0)
            return new("chat", Warning, $"HTTP 200 with an empty answer (finish_reason={finish ?? "unknown"}).");
        return new("chat", Ok, $"Answered (finish_reason={finish ?? "unknown"}).");
    }

    public static ProbeCheck Reasoning(int status, string body)
    {
        if (status != 200) return new("reasoning", Inconclusive, "Not checked: the chat request did not succeed.");
        var message = FirstMessage(body, out _);
        if (message is null) return new("reasoning", Inconclusive, "Not checked: no message to inspect.");
        var content = TextOf(message["content"]);
        if (ReasoningInText.Any(marker => content.Contains(marker, StringComparison.Ordinal)))
            return new("reasoning", Failed,
                "Reasoning markers appear inside the answer. The runtime is not separating reasoning: a reasoning parser is missing for this model.");
        if (ReasoningOf(message).Length > 0)
            return new("reasoning", Ok, "Reasoning is returned separately from the answer (message.reasoning or reasoning_content).");
        return new("reasoning", Inconclusive, "No reasoning trace appeared for this prompt: either the model did not think or it has no reasoning mode.");
    }

    public static ProbeCheck Tools(int status, string body)
    {
        if (status == 400 && Mentions(body, "tool") && (Mentions(body, "enable-auto-tool-choice") ||
                Mentions(body, "tool-call-parser") || Mentions(body, "tool choice")))
            return new("tools", Failed,
                "The server refuses tools: it was started without tool calling (vLLM needs --enable-auto-tool-choice and a --tool-call-parser).");
        if (status != 200) return new("tools", Failed, HttpFailure(status, body));

        var message = FirstMessage(body, out _);
        if (message is null) return new("tools", Failed, "HTTP 200 but the answer has no choices[0].message.");
        if (message["tool_calls"] is JsonArray { Count: > 0 } calls)
        {
            var function = calls[0]?["function"];
            var name = function?["name"]?.GetValue<string>();
            var arguments = function?["arguments"];
            var parsesAsJson = arguments switch
            {
                JsonObject => true,
                JsonValue value when value.TryGetValue<string>(out var text) => IsJsonObject(text),
                _ => false
            };
            return name == "get_weather" && parsesAsJson
                ? new("tools", Ok, "The model called get_weather with valid JSON arguments through tool_calls.")
                : new("tools", Warning, "tool_calls is present but the call or its arguments are malformed.");
        }

        var content = TextOf(message["content"]);
        if (ToolSyntaxInText.Any(marker => content.Contains(marker, StringComparison.Ordinal)))
            return new("tools", Failed,
                "The model wrote tool-call syntax as plain text. The tool-call parser is missing or wrong for this model.");
        return new("tools", Inconclusive, "The model answered without calling the tool; this does not show whether the parser is configured.");
    }

    public static ProbeCheck Streaming(int status, string contentType, string body)
    {
        if (status != 200) return new("streaming", Failed, HttpFailure(status, body));
        if (!contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return new("streaming", Failed, $"Expected text/event-stream, got '{contentType}'.");
        var dataLines = body.Split('\n').Count(line => line.StartsWith("data:", StringComparison.Ordinal));
        if (dataLines == 0) return new("streaming", Failed, "The stream carried no data events.");
        return body.Contains("[DONE]", StringComparison.Ordinal)
            ? new("streaming", Ok, $"Real SSE stream: {dataLines} data events ended by [DONE].")
            : new("streaming", Warning, $"SSE events arrived ({dataLines}) but the stream did not end with [DONE].");
    }

    private static JsonObject? FirstMessage(string body, out string? finishReason)
    {
        finishReason = null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root || root["choices"] is not JsonArray { Count: > 0 } choices ||
                choices[0] is not JsonObject choice) return null;
            finishReason = choice["finish_reason"]?.GetValue<string>();
            return choice["message"] as JsonObject;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string ReasoningOf(JsonObject message) =>
        TextOf(message["reasoning"]) is { Length: > 0 } modern ? modern : TextOf(message["reasoning_content"]);

    private static string TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;

    private static bool IsJsonObject(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject; }
        catch (JsonException) { return false; }
    }

    private static bool Mentions(string body, string fragment) =>
        body.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static string HttpFailure(int status, string body)
    {
        var message = string.Empty;
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            message = error is JsonObject obj ? TextOf(obj["message"]) : TextOf(error);
            if (message.Length == 0) message = TextOf(JsonNode.Parse(body)?["message"]);
        }
        catch (JsonException) { /* not JSON: use the raw text below */ }

        if (message.Length == 0) message = body.Trim();
        message = message.Replace('\n', ' ').Replace('\r', ' ');
        return $"HTTP {status}: {(message.Length > 220 ? message[..220] + "…" : message)}".TrimEnd(' ', ':');
    }
}
