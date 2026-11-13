using System.Text.Json.Nodes;

namespace LlmProxy.Api.Multimodal;

/// <summary>
/// Validates the <c>input</c> of <c>POST /v1/embeddings</c> the way OpenAI documents it: a string, an array of strings,
/// an array of token ids, or an array of token-id arrays. Empty values and oversized batches are refused here so a
/// mistake never reaches the GPU.
/// </summary>
public static class EmbeddingInput
{
    public const int MaxItems = 2048;

    public static bool IsValid(JsonNode? input, out string? problem)
    {
        problem = null;
        switch (input)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (string.IsNullOrWhiteSpace(text)) { problem = "input must not be empty."; return false; }
                return true;
            case JsonArray { Count: 0 }:
                problem = "input must not be an empty array.";
                return false;
            case JsonArray items when items.Count > MaxItems:
                problem = $"input may contain at most {MaxItems} items per request.";
                return false;
            case JsonArray items:
                var allStrings = items.All(item => item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s));
                var allTokens = items.All(item => item is JsonValue v && v.TryGetValue<int>(out _));
                var allTokenArrays = items.All(item => item is JsonArray { Count: > 0 } row && row.All(token => token is JsonValue v && v.TryGetValue<int>(out _)));
                if (allStrings || allTokens || allTokenArrays) return true;
                problem = "input must be a string, an array of non-empty strings, an array of token ids, or an array of token-id arrays.";
                return false;
            default:
                problem = "input is required: a string, an array of strings, or token ids.";
                return false;
        }
    }
}
