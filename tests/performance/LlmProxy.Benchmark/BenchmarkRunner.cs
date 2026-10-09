using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LlmProxy.Benchmarking;

public sealed class BenchmarkRunner(HttpClient httpClient)
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<BenchmarkReport> RunAsync(BenchmarkOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var bearerToken = ResolveBearerToken(options.ApiKeyEnvironmentVariable);
        var endpoint = BenchmarkEndpointBuilder.Build(options.Target, options.Surface);

        for (var warmupIndex = 0; warmupIndex < options.WarmupRequests; warmupIndex++)
        {
            var warmup = await ExecuteRequestAsync(endpoint, options, bearerToken, cancellationToken);
            if (!warmup.Success)
            {
                throw new InvalidOperationException($"Warm-up request failed with status {warmup.StatusCode} ({warmup.ErrorCode ?? "unknown_error"}).");
            }
        }

        var levels = new List<BenchmarkLevelReport>(options.ConcurrencyLevels.Length);
        for (var levelIndex = 0; levelIndex < options.ConcurrencyLevels.Length; levelIndex++)
        {
            if (levelIndex > 0 && options.DelayBetweenLevels > TimeSpan.Zero)
            {
                await Task.Delay(options.DelayBetweenLevels, cancellationToken);
            }

            levels.Add(await RunLevelAsync(endpoint, options, bearerToken, options.ConcurrencyLevels[levelIndex], cancellationToken));
        }

        var report = new BenchmarkReport(
            runId,
            startedAt,
            DateTimeOffset.UtcNow,
            options.Target.AbsoluteUri.TrimEnd('/'),
            options.Model,
            options.Surface == BenchmarkSurface.ChatCompletions ? "chat_completions" : "responses",
            options.Streaming,
            options.RequestsPerLevel,
            options.WarmupRequests,
            options.MaxOutputTokens,
            options.PromptLabel,
            options.Prompt.Length,
            levels);
        return report with { Recommendation = BenchmarkCapacityEvaluator.Evaluate(report, options) };
    }

    private async Task<BenchmarkLevelReport> RunLevelAsync(
        Uri endpoint,
        BenchmarkOptions options,
        string? bearerToken,
        int concurrency,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var levelClock = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, options.RequestsPerLevel)
            .Select(async _ =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await ExecuteRequestAsync(endpoint, options, bearerToken, cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);
        levelClock.Stop();

        var successful = results.Where(result => result.Success).ToArray();
        var ttftValues = successful
            .Where(result => result.TtftMilliseconds.HasValue)
            .Select(result => result.TtftMilliseconds!.Value)
            .ToArray();
        var durationValues = successful.Select(result => result.DurationMilliseconds).ToArray();
        var wallClockSeconds = Math.Max(levelClock.Elapsed.TotalSeconds, 0.000001d);
        var inputTokens = successful.Sum(result => result.InputTokens ?? 0L);
        var outputTokens = successful.Sum(result => result.OutputTokens ?? 0L);
        var totalTokens = successful.Sum(result => result.TotalTokens ?? 0L);
        var tokenObservedRequests = successful.Count(result => result.InputTokens.HasValue || result.OutputTokens.HasValue || result.TotalTokens.HasValue);
        var errors = results
            .Where(result => !result.Success)
            .GroupBy(result => result.ErrorCode ?? $"http_{result.StatusCode}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return new BenchmarkLevelReport(
            concurrency,
            results.Length,
            successful.Length,
            results.Length - successful.Length,
            results.Length == 0 ? 0d : successful.Length * 100d / results.Length,
            levelClock.Elapsed.TotalMilliseconds,
            successful.Length / wallClockSeconds,
            BenchmarkStatistics.Percentile(ttftValues, 0.50d),
            BenchmarkStatistics.Percentile(ttftValues, 0.95d),
            BenchmarkStatistics.Percentile(ttftValues, 0.99d),
            BenchmarkStatistics.Percentile(durationValues, 0.50d),
            BenchmarkStatistics.Percentile(durationValues, 0.95d),
            BenchmarkStatistics.Percentile(durationValues, 0.99d),
            inputTokens,
            outputTokens,
            totalTokens,
            tokenObservedRequests,
            tokenObservedRequests > 0 ? outputTokens / wallClockSeconds : null,
            errors);
    }

    private async Task<BenchmarkRequestResult> ExecuteRequestAsync(
        Uri endpoint,
        BenchmarkOptions options,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        request.Content = new StringContent(BuildPayload(options), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var clock = Stopwatch.StartNew();

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var requestId = TryGetRequestId(response);
            if (!response.IsSuccessStatusCode)
            {
                clock.Stop();
                return new BenchmarkRequestResult(
                    false,
                    (int)response.StatusCode,
                    clock.Elapsed.TotalMilliseconds,
                    null,
                    null,
                    null,
                    null,
                    requestId,
                    $"http_{(int)response.StatusCode}");
            }

            return options.Streaming
                ? await ReadStreamingResponseAsync(response, options.Surface, clock, requestId, timeout.Token)
                : await ReadBufferedResponseAsync(response, clock, requestId, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            clock.Stop();
            return new BenchmarkRequestResult(false, 0, clock.Elapsed.TotalMilliseconds, null, null, null, null, null, "timeout");
        }
        catch (HttpRequestException)
        {
            clock.Stop();
            return new BenchmarkRequestResult(false, 0, clock.Elapsed.TotalMilliseconds, null, null, null, null, null, "transport_error");
        }
    }

    private static async Task<BenchmarkRequestResult> ReadStreamingResponseAsync(
        HttpResponseMessage response,
        BenchmarkSurface surface,
        Stopwatch clock,
        string? requestId,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: false);

        double? ttftMilliseconds = null;
        long? inputTokens = null;
        long? outputTokens = null;
        long? totalTokens = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0 || data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ttftMilliseconds is null && OpenAiUsageParser.ContainsOutputDelta(data, surface))
            {
                ttftMilliseconds = clock.Elapsed.TotalMilliseconds;
            }

            var usage = OpenAiUsageParser.Parse(data);
            inputTokens = usage.InputTokens ?? inputTokens;
            outputTokens = usage.OutputTokens ?? outputTokens;
            totalTokens = usage.TotalTokens ?? totalTokens;
        }

        clock.Stop();
        return new BenchmarkRequestResult(
            true,
            (int)response.StatusCode,
            clock.Elapsed.TotalMilliseconds,
            ttftMilliseconds,
            inputTokens,
            outputTokens,
            totalTokens,
            requestId,
            null);
    }

    private static async Task<BenchmarkRequestResult> ReadBufferedResponseAsync(
        HttpResponseMessage response,
        Stopwatch clock,
        string? requestId,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        double? firstBodyByteMilliseconds = null;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            firstBodyByteMilliseconds ??= clock.Elapsed.TotalMilliseconds;
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        clock.Stop();
        var usage = OpenAiUsageParser.Parse(Encoding.UTF8.GetString(body.ToArray()));
        return new BenchmarkRequestResult(
            true,
            (int)response.StatusCode,
            clock.Elapsed.TotalMilliseconds,
            firstBodyByteMilliseconds,
            usage.InputTokens,
            usage.OutputTokens,
            usage.TotalTokens,
            requestId,
            null);
    }

    private static string BuildPayload(BenchmarkOptions options)
    {
        object payload = options.Surface switch
        {
            BenchmarkSurface.ChatCompletions => new
            {
                model = options.Model,
                messages = new[] { new { role = "user", content = options.Prompt } },
                stream = options.Streaming,
                stream_options = options.Streaming ? new { include_usage = true } : null,
                max_tokens = options.MaxOutputTokens,
                temperature = 0
            },
            BenchmarkSurface.Responses => new
            {
                model = options.Model,
                input = options.Prompt,
                stream = options.Streaming,
                max_output_tokens = options.MaxOutputTokens,
                temperature = 0
            },
            _ => throw new ArgumentOutOfRangeException(nameof(options.Surface))
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string? ResolveBearerToken(string? environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
        {
            return null;
        }

        var token = Environment.GetEnvironmentVariable(environmentVariable);
        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new InvalidOperationException($"Environment variable '{environmentVariable}' is not set or is empty.");
    }

    private static string? TryGetRequestId(HttpResponseMessage response)
    {
        foreach (var header in new[] { "X-LlmProxy-Request-Id", "x-request-id", "request-id" })
        {
            if (response.Headers.TryGetValues(header, out var values))
            {
                return values.FirstOrDefault();
            }
        }

        return null;
    }
}
