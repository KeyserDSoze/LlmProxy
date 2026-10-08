using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmProxy.Api.Observability;

/// <summary>
/// Incrementally reconstructs the semantic response from an OpenAI-compatible SSE stream.
/// The original SSE bytes are forwarded to the caller separately; they are never retained
/// in the request audit. This class is deliberately request-scoped and not thread-safe.
/// </summary>
public sealed class StreamingAuditAssembler(string surface, int maxCapturedCharacters = 2_000_000)
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly List<string> _data = [];
    private readonly Dictionary<int, JsonObject> _chatChoices = [];
    private readonly SortedDictionary<int, JsonObject> _responseItems = [];
    private readonly Dictionary<(JsonObject Target, string Property), StringBuilder> _fragments = [];
    private readonly int _limit = Math.Max(1, maxCapturedCharacters);
    private string? _eventName;
    private string? _id;
    private string? _model;
    private JsonNode? _usage;
    private JsonObject? _responseRoot;
    private JsonObject? _terminalResponse;
    private string? _failureReason;
    private bool _previousWasCr;
    private bool _done;
    private bool _completed;
    private bool _failed;
    private bool _reportedIncomplete;
    private bool _truncated;
    private int _capturedCharacters;
    private int _eventCount;
    private int _invalidEvents;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        // Bounded temporary decoding; UTF-8 multibyte sequences can span writes.
        var buffer = new char[8192];
        for (var offset = 0; offset < bytes.Length; offset += 4096)
        {
            var part = bytes.Slice(offset, Math.Min(4096, bytes.Length - offset));
            var decoded = _decoder.GetChars(part, buffer, flush: false);
            for (var index = 0; index < decoded; index++)
            {
                var value = buffer[index];
                if (value == '\r')
                {
                    EndLine();
                    _previousWasCr = true;
                }
                else if (value == '\n')
                {
                    if (!_previousWasCr) EndLine();
                    _previousWasCr = false;
                }
                else
                {
                    _previousWasCr = false;
                    if (_line.Length < 262_144) _line.Append(value);
                    else _truncated = true;
                }
            }
        }
    }

    /// <summary>Return a compact, valid JSON document even for cancelled/failed streams.</summary>
    public string Build(bool cancelled = false, string? upstreamFailure = null)
    {
        // Do not fabricate the terminating blank line: only complete SSE events count.
        foreach (var (target, property) in _fragments)
        {
            target[property] = _fragments[(target, property)].ToString();
        }

        var terminal = surface == "responses" ? _completed : _done;
        var reason = upstreamFailure ?? _failureReason;
        var state = _failed || !string.IsNullOrEmpty(upstreamFailure) ? "interrupted"
            : terminal ? "completed"
            : cancelled ? "cancelled"
            : "incomplete";

        JsonObject response;
        if (surface == "responses")
        {
            response = _terminalResponse?.DeepClone() as JsonObject
                ?? _responseRoot?.DeepClone() as JsonObject
                ?? new JsonObject();
            if (_terminalResponse is null)
            {
                var output = new JsonArray();
                foreach (var (_, item) in _responseItems) output.Add(item.DeepClone());
                response["output"] = output;
            }
            if (response["object"] is null) response["object"] = "response";
        }
        else
        {
            var choices = new JsonArray();
            foreach (var (_, choice) in _chatChoices.OrderBy(item => item.Key))
                choices.Add(choice.DeepClone());
            response = new JsonObject
            {
                ["id"] = _id,
                ["object"] = "chat.completion",
                ["model"] = _model,
                ["choices"] = choices,
                ["usage"] = _usage?.DeepClone()
            };
        }

        return new JsonObject
        {
            ["format"] = "llmproxy.audit.stream.v1",
            ["streaming"] = true,
            ["state"] = state,
            ["complete"] = state == "completed",
            ["reason"] = reason ?? (state == "cancelled" ? "client_cancelled" : null),
            ["eventsProcessed"] = _eventCount,
            ["invalidEvents"] = _invalidEvents,
            ["truncated"] = _truncated,
            ["response"] = response
        }.ToJsonString();
    }

    private void EndLine()
    {
        var line = _line.ToString();
        _line.Clear();
        if (line.Length == 0)
        {
            EndEvent();
            return;
        }

        if (line.StartsWith(':')) return; // SSE heartbeat/comment.
        if (line.StartsWith("data:", StringComparison.Ordinal))
            _data.Add(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
        else if (line.StartsWith("event:", StringComparison.Ordinal))
            _eventName = line[6..].Trim();
    }

    private void EndEvent()
    {
        if (_data.Count == 0) { _eventName = null; return; }
        var raw = string.Join("\n", _data);
        _data.Clear();
        var eventName = _eventName;
        _eventName = null;
        _eventCount++;

        if (raw == "[DONE]") { _done = true; return; }
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject item) { _invalidEvents++; return; }
            if (surface == "responses") ProcessResponses(item, eventName);
            else ProcessChat(item, eventName);
        }
        catch (JsonException) { _invalidEvents++; }
        catch (InvalidOperationException) { _invalidEvents++; }
    }

    private void ProcessChat(JsonObject item, string? eventName)
    {
        if (eventName == "error" || item["error"] is not null)
        {
            _failed = true;
            _failureReason = "upstream_error";
            return;
        }

        _id ??= AsString(item["id"]);
        _model ??= AsString(item["model"]);
        if (item["usage"] is not null) _usage = LimitedClone(item["usage"]);

        if (item["choices"] is not JsonArray choices) return;
        foreach (var candidate in choices)
        {
            if (candidate is not JsonObject choice) continue;
            var index = AsInt(choice["index"]) ?? 0;
            if (!_chatChoices.TryGetValue(index, out var output))
            {
                output = new JsonObject
                {
                    ["index"] = index,
                    ["message"] = new JsonObject { ["role"] = "assistant" },
                    ["finish_reason"] = null
                };
                _chatChoices.Add(index, output);
            }
            var message = (JsonObject)output["message"]!;
            var delta = choice["delta"] as JsonObject ?? choice["message"] as JsonObject;
            if (delta is not null)
            {
                if (AsString(delta["role"]) is { } role) message["role"] = role;
                AppendFragment(message, "content", AsString(delta["content"]));
                AppendFragment(message, "reasoning_content", AsString(delta["reasoning_content"]));
                AppendFragment(message, "refusal", AsString(delta["refusal"]));

                if (delta["tool_calls"] is JsonArray tools)
                {
                    if (message["tool_calls"] is not JsonArray) message["tool_calls"] = new JsonArray();
                    var resultTools = (JsonArray)message["tool_calls"]!;
                    foreach (var value in tools)
                    {
                        if (value is not JsonObject tool) continue;
                        var toolIndex = AsInt(tool["index"]) ?? 0;
                        var target = EnsureArrayObject(resultTools, toolIndex);
                        if (AsString(tool["id"]) is { } toolId) target["id"] = toolId;
                        if (AsString(tool["type"]) is { } type) target["type"] = type;
                        if (tool["function"] is JsonObject function)
                        {
                            if (target["function"] is not JsonObject) target["function"] = new JsonObject();
                            var outputFunction = (JsonObject)target["function"]!;
                            if (AsString(function["name"]) is { } name) outputFunction["name"] = name;
                            AppendFragment(outputFunction, "arguments", AsString(function["arguments"]));
                        }
                    }
                }
                if (delta["function_call"] is JsonObject legacy)
                {
                    if (message["function_call"] is not JsonObject) message["function_call"] = new JsonObject();
                    var function = (JsonObject)message["function_call"]!;
                    if (AsString(legacy["name"]) is { } name) function["name"] = name;
                    AppendFragment(function, "arguments", AsString(legacy["arguments"]));
                }
            }
            if (choice["finish_reason"] is not null)
                output["finish_reason"] = choice["finish_reason"]!.DeepClone();
        }
    }

    private void ProcessResponses(JsonObject item, string? eventName)
    {
        var type = AsString(item["type"]) ?? eventName;
        if (type is "error" or "response.failed")
        {
            _failed = true;
            _failureReason = type;
        }
        if (type == "response.incomplete")
        {
            _reportedIncomplete = true;
            _failureReason = "response.incomplete";
        }
        if (item["response"] is JsonObject snapshot && type is
            "response.created" or "response.in_progress" or "response.completed" or "response.failed" or "response.incomplete")
        {
            var clone = LimitedClone(snapshot) as JsonObject;
            if (clone is not null)
            {
                _responseRoot = clone;
                if (type == "response.completed") _terminalResponse = clone;
            }
        }
        if (type == "response.completed") _completed = true;

        var outputIndex = AsInt(item["output_index"]) ?? 0;
        if (type is "response.output_item.added" or "response.output_item.done")
        {
            if (item["item"] is JsonObject outputItem && LimitedClone(outputItem) is JsonObject copy)
                _responseItems[outputIndex] = copy;
            return;
        }

        if (type is null || !type.StartsWith("response.", StringComparison.Ordinal)) return;
        if (type is not ("response.output_text.delta" or "response.output_text.done" or
            "response.refusal.delta" or "response.refusal.done" or
            "response.function_call_arguments.delta" or "response.function_call_arguments.done" or
            "response.content_part.added" or "response.content_part.done" or
            "response.reasoning_summary_text.delta" or "response.reasoning_summary_text.done"))
            return;

        if (!_responseItems.TryGetValue(outputIndex, out var output))
        {
            output = new JsonObject();
            _responseItems[outputIndex] = output;
        }
        if (type.StartsWith("response.function_call_arguments.", StringComparison.Ordinal))
        {
            if (type.EndsWith(".delta", StringComparison.Ordinal))
                AppendFragment(output, "arguments", AsString(item["delta"]));
            else SetFinal(output, "arguments", AsString(item["arguments"]));
            return;
        }

        var contentIndex = AsInt(item["content_index"]) ?? AsInt(item["summary_index"]) ?? 0;
        var key = type.StartsWith("response.reasoning_summary_text.", StringComparison.Ordinal) ? "summary" : "content";
        if (output[key] is not JsonArray) output[key] = new JsonArray();
        var content = (JsonArray)output[key]!;
        var part = EnsureArrayObject(content, contentIndex);

        if (type is "response.content_part.added" or "response.content_part.done")
        {
            if (item["part"] is JsonObject source && LimitedClone(source) is JsonObject copy)
                content[contentIndex] = copy;
            return;
        }

        var property = type.StartsWith("response.refusal.", StringComparison.Ordinal) ? "refusal" : "text";
        if (type.EndsWith(".delta", StringComparison.Ordinal))
        {
            if (part["type"] is null)
                part["type"] = property == "refusal" ? "refusal" : "output_text";
            AppendFragment(part, property, AsString(item["delta"]));
        }
        else SetFinal(part, property, AsString(item[property]));
    }

    private void AppendFragment(JsonObject target, string property, string? fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return;
        var available = _limit - _capturedCharacters;
        if (available <= 0) { _truncated = true; return; }
        var take = Math.Min(fragment.Length, available);
        var key = (target, property);
        if (!_fragments.TryGetValue(key, out var builder))
        {
            builder = new StringBuilder(AsString(target[property]));
            _fragments.Add(key, builder);
        }
        builder.Append(fragment.AsSpan(0, take));
        _capturedCharacters += take;
        if (take != fragment.Length) _truncated = true;
    }

    private void SetFinal(JsonObject target, string property, string? final)
    {
        if (final is null) return;
        if (final.Length > _limit) { _truncated = true; return; }
        _fragments.Remove((target, property));
        target[property] = final;
    }

    private JsonNode? LimitedClone(JsonNode? node)
    {
        if (node is null) return null;
        if (node.ToJsonString().Length <= _limit) return node.DeepClone();
        _truncated = true;
        return null;
    }

    private static JsonObject EnsureArrayObject(JsonArray array, int index)
    {
        // A malicious upstream cannot force us to allocate an enormous sparse array.
        index = Math.Clamp(index, 0, 1024);
        while (array.Count <= index) array.Add(new JsonObject());
        if (array[index] is not JsonObject) array[index] = new JsonObject();
        return (JsonObject)array[index]!;
    }

    private static string? AsString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static int? AsInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;
}
