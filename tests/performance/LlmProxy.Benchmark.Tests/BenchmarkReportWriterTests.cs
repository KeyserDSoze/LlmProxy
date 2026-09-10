using LlmProxy.Benchmarking;

namespace LlmProxy.Benchmark.Tests;

public sealed class BenchmarkReportWriterTests
{
    [Fact]
    public void Serialized_report_contains_metadata_but_no_prompt_or_secret_fields()
    {
        var report = new BenchmarkReport(
            "run-1",
            DateTimeOffset.Parse("2026-09-10T08:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T08:01:00Z"),
            "http://localhost:8080",
            "agic-code",
            "chat_completions",
            true,
            10,
            2,
            128,
            "synthetic-coding-v1",
            100,
            new[]
            {
                new BenchmarkLevelReport(1, 10, 10, 0, 100, 1000, 10, 100, 150, 160, 500, 600, 650, 100, 50, 150, 10, 50, new Dictionary<string, int>())
            });

        var json = BenchmarkReportWriter.SerializeJson(report);

        Assert.Contains("agic-code", json);
        Assert.Contains("synthetic-coding-v1", json);
        Assert.DoesNotContain("prompt\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer", json, StringComparison.OrdinalIgnoreCase);
    }
}
