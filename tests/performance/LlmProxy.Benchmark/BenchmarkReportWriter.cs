using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LlmProxy.Benchmarking;

public static class BenchmarkReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string SerializeJson(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static async Task<(string JsonPath, string CsvPath)> WriteAsync(
        BenchmarkReport report,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var fullDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullDirectory);

        var prefix = $"llmproxy-benchmark-{report.RunId}";
        var jsonPath = Path.Combine(fullDirectory, prefix + ".json");
        var csvPath = Path.Combine(fullDirectory, prefix + ".csv");

        await File.WriteAllTextAsync(jsonPath, SerializeJson(report), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(csvPath, SerializeCsv(report), Encoding.UTF8, cancellationToken);
        return (jsonPath, csvPath);
    }

    public static string SerializeCsv(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("run_id,target,model,surface,streaming,prompt_label,concurrency,attempted,succeeded,failed,success_rate_percent,wall_clock_ms,requests_per_second,p50_ttft_ms,p95_ttft_ms,p99_ttft_ms,p50_duration_ms,p95_duration_ms,p99_duration_ms,input_tokens,output_tokens,total_tokens,token_observed_requests,output_tokens_per_second,error_breakdown");

        foreach (var level in report.Levels)
        {
            var errors = string.Join(';', level.ErrorBreakdown.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"));
            var fields = new[]
            {
                report.RunId,
                report.Target,
                report.Model,
                report.Surface,
                report.Streaming.ToString(CultureInfo.InvariantCulture),
                report.PromptLabel,
                level.Concurrency.ToString(CultureInfo.InvariantCulture),
                level.Attempted.ToString(CultureInfo.InvariantCulture),
                level.Succeeded.ToString(CultureInfo.InvariantCulture),
                level.Failed.ToString(CultureInfo.InvariantCulture),
                Format(level.SuccessRatePercent),
                Format(level.WallClockMilliseconds),
                Format(level.RequestsPerSecond),
                Format(level.P50TtftMilliseconds),
                Format(level.P95TtftMilliseconds),
                Format(level.P99TtftMilliseconds),
                Format(level.P50DurationMilliseconds),
                Format(level.P95DurationMilliseconds),
                Format(level.P99DurationMilliseconds),
                level.InputTokens.ToString(CultureInfo.InvariantCulture),
                level.OutputTokens.ToString(CultureInfo.InvariantCulture),
                level.TotalTokens.ToString(CultureInfo.InvariantCulture),
                level.TokenObservedRequests.ToString(CultureInfo.InvariantCulture),
                Format(level.OutputTokensPerSecond),
                errors
            };

            builder.AppendLine(string.Join(',', fields.Select(Escape)));
        }

        return builder.ToString();
    }

    private static string Format(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public static class BenchmarkConsoleWriter
{
    public static void Write(BenchmarkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Console.WriteLine($"Run:       {report.RunId}");
        Console.WriteLine($"Target:    {report.Target}");
        Console.WriteLine($"Model:     {report.Model}");
        Console.WriteLine($"Surface:   {report.Surface} ({(report.Streaming ? "streaming" : "non-streaming")})");
        Console.WriteLine($"Prompt:    {report.PromptLabel} ({report.PromptCharacters} chars; body not stored)");
        Console.WriteLine();
        Console.WriteLine(" conc | ok/total | success | req/s | p50 TTFT | p95 TTFT | p95 duration | out tok/s");
        Console.WriteLine("------+----------+---------+-------+----------+----------+--------------+----------");

        foreach (var level in report.Levels)
        {
            Console.WriteLine(
                $"{level.Concurrency,5} | {level.Succeeded,2}/{level.Attempted,-5} | {level.SuccessRatePercent,6:0.0}% | {level.RequestsPerSecond,5:0.00} | {Format(level.P50TtftMilliseconds),8} | {Format(level.P95TtftMilliseconds),8} | {Format(level.P95DurationMilliseconds),12} | {FormatRate(level.OutputTokensPerSecond),8}");
        }
    }

    private static string Format(double? value) => value is null ? "—" : $"{value.Value:0} ms";
    private static string FormatRate(double? value) => value is null ? "—" : $"{value.Value:0.0}";
}
