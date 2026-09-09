using System.Text;
using System.Text.Json;

namespace LlmProxy.Api.OpenAi;

internal sealed class OpenAiResponseObserver(bool streaming, Func<long> elapsedMilliseconds)
{
    private const int MaxNonStreamingCaptureBytes = 2 * 1024 * 1024;
    private const int MaxSseLineBytes = 256 * 1024;

    private readonly MemoryStream _buffer = new();
    private bool _bufferOverflowed;
    private TokenUsage? _usage;

    public bool IsStreaming { get; } = streaming;
    public long? TimeToFirstByteMilliseconds { get; private set; }
    public TokenUsage? Usage => _usage;

    public void Observe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        TimeToFirstByteMilliseconds ??= Math.Max(0, elapsedMilliseconds());

        if (IsStreaming)
        {
            ObserveSse(bytes);
        }
        else
        {
            ObserveJson(bytes);
        }
    }

    public void Complete()
    {
        if (IsStreaming)
        {
            if (_buffer.Length > 0 && !_bufferOverflowed)
            {
                ProcessSseLine();
            }

            _buffer.SetLength(0);
            _bufferOverflowed = false;
            return;
        }

        if (_bufferOverflowed || _buffer.Length == 0)
        {
            return;
        }

        _usage = OpenAiUsageParser.TryParse(_buffer.ToArray()) ?? _usage;
    }

    private void ObserveJson(ReadOnlySpan<byte> bytes)
    {
        if (_bufferOverflowed)
        {
            return;
        }

        if (_buffer.Length + bytes.Length > MaxNonStreamingCaptureBytes)
        {
            _buffer.SetLength(0);
            _bufferOverflowed = true;
            return;
        }

        _buffer.Write(bytes);
    }

    private void ObserveSse(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            if (newline < 0)
            {
                AppendSseLineSegment(bytes);
                return;
            }

            AppendSseLineSegment(bytes[..newline]);
            if (!_bufferOverflowed)
            {
                ProcessSseLine();
            }

            _buffer.SetLength(0);
            _bufferOverflowed = false;
            bytes = bytes[(newline + 1)..];
        }
    }

    private void AppendSseLineSegment(ReadOnlySpan<byte> bytes)
    {
        if (_bufferOverflowed || bytes.IsEmpty)
        {
            return;
        }

        if (_buffer.Length + bytes.Length > MaxSseLineBytes)
        {
            _buffer.SetLength(0);
            _bufferOverflowed = true;
            return;
        }

        _buffer.Write(bytes);
    }

    private void ProcessSseLine()
    {
        var lineBytes = _buffer.ToArray();
        var length = lineBytes.Length;
        if (length > 0 && lineBytes[length - 1] == '\r')
        {
            length--;
        }

        if (length == 0)
        {
            return;
        }

        var line = Encoding.UTF8.GetString(lineBytes, 0, length);
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        var payload = line[5..].TrimStart();
        if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
        {
            return;
        }

        _usage = OpenAiUsageParser.TryParse(payload) ?? _usage;
    }
}

internal sealed record TokenUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);

internal static class OpenAiUsageParser
{
    public static TokenUsage? TryParse(byte[] utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            return FindUsage(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static TokenUsage? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindUsage(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TokenUsage? FindUsage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                var usage = ParseUsageObject(usageElement);
                if (usage is not null)
                {
                    return usage;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "usage", StringComparison.Ordinal))
                {
                    continue;
                }

                var nested = FindUsage(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindUsage(item);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static TokenUsage? ParseUsageObject(JsonElement usage)
    {
        var inputTokens = GetInt32(usage, "input_tokens") ?? GetInt32(usage, "prompt_tokens");
        var outputTokens = GetInt32(usage, "output_tokens") ?? GetInt32(usage, "completion_tokens");
        var totalTokens = GetInt32(usage, "total_tokens");

        if (totalTokens is null && inputTokens is int input && outputTokens is int output)
        {
            totalTokens = input + output;
        }

        return inputTokens is null && outputTokens is null && totalTokens is null
            ? null
            : new TokenUsage(inputTokens, outputTokens, totalTokens);
    }

    private static int? GetInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var parsed) ? parsed : null;
    }
}
