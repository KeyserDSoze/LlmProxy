using System.Text.Json;

namespace LlmProxy.Benchmarking;

public static class OpenAiUsageParser
{
    public static OpenAiTokenUsage Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new OpenAiTokenUsage(null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return new OpenAiTokenUsage(null, null, null);
        }
    }

    public static bool ContainsOutputDelta(string json, BenchmarkSurface surface)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return surface switch
            {
                BenchmarkSurface.ChatCompletions => ChatContainsOutput(root),
                BenchmarkSurface.Responses => ResponsesContainsOutput(root),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static OpenAiTokenUsage Parse(JsonElement root)
    {
        JsonElement usage;
        if (!root.TryGetProperty("usage", out usage))
        {
            if (!root.TryGetProperty("response", out var response) ||
                response.ValueKind != JsonValueKind.Object ||
                !response.TryGetProperty("usage", out usage))
            {
                return new OpenAiTokenUsage(null, null, null);
            }
        }

        if (usage.ValueKind != JsonValueKind.Object)
        {
            return new OpenAiTokenUsage(null, null, null);
        }

        var input = ReadInt64(usage, "input_tokens") ?? ReadInt64(usage, "prompt_tokens");
        var output = ReadInt64(usage, "output_tokens") ?? ReadInt64(usage, "completion_tokens");
        var total = ReadInt64(usage, "total_tokens");
        if (total is null && input is not null && output is not null)
        {
            total = input + output;
        }

        return new OpenAiTokenUsage(input, output, total);
    }

    private static long? ReadInt64(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out number)
            ? number
            : null;
    }

    private static bool ChatContainsOutput(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (HasNonEmptyString(delta, "content") || HasNonEmptyString(delta, "reasoning_content"))
            {
                return true;
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array && toolCalls.GetArrayLength() > 0)
            {
                return true;
            }

            if (delta.TryGetProperty("function_call", out var functionCall) && functionCall.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ResponsesContainsOutput(JsonElement root)
    {
        if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var eventType = type.GetString();
            if (eventType?.EndsWith(".delta", StringComparison.OrdinalIgnoreCase) == true &&
                root.TryGetProperty("delta", out var delta))
            {
                return delta.ValueKind switch
                {
                    JsonValueKind.String => !string.IsNullOrEmpty(delta.GetString()),
                    JsonValueKind.Object => true,
                    JsonValueKind.Array => delta.GetArrayLength() > 0,
                    _ => false
                };
            }
        }

        return HasNonEmptyString(root, "output_text");
    }

    private static bool HasNonEmptyString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(property.GetString());
}
