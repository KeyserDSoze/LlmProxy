namespace LlmProxy.Benchmarking;

public sealed record BenchmarkReport(
    string RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Target,
    string Model,
    string Surface,
    bool Streaming,
    int RequestsPerLevel,
    int WarmupRequests,
    int MaxOutputTokens,
    string PromptLabel,
    int PromptCharacters,
    IReadOnlyList<BenchmarkLevelReport> Levels);

public sealed record BenchmarkLevelReport(
    int Concurrency,
    int Attempted,
    int Succeeded,
    int Failed,
    double SuccessRatePercent,
    double WallClockMilliseconds,
    double RequestsPerSecond,
    double? P50TtftMilliseconds,
    double? P95TtftMilliseconds,
    double? P99TtftMilliseconds,
    double? P50DurationMilliseconds,
    double? P95DurationMilliseconds,
    double? P99DurationMilliseconds,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    int TokenObservedRequests,
    double? OutputTokensPerSecond,
    IReadOnlyDictionary<string, int> ErrorBreakdown);

internal sealed record BenchmarkRequestResult(
    bool Success,
    int StatusCode,
    double DurationMilliseconds,
    double? TtftMilliseconds,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    string? RequestId,
    string? ErrorCode);

public sealed record OpenAiTokenUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);
