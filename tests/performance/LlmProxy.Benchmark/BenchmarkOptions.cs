using System.Globalization;

namespace LlmProxy.Benchmarking;

public enum BenchmarkSurface
{
    ChatCompletions,
    Responses
}

public sealed class BenchmarkOptions
{
    private const string DefaultSyntheticPrompt = "Write a TypeScript function named add that accepts two integers and returns their sum. Return only the function.";

    public required Uri Target { get; init; }
    public required string Model { get; init; }
    public BenchmarkSurface Surface { get; init; } = BenchmarkSurface.ChatCompletions;
    public bool Streaming { get; init; } = true;
    public required int[] ConcurrencyLevels { get; init; }
    public int RequestsPerLevel { get; init; } = 10;
    public int WarmupRequests { get; init; } = 2;
    public int MaxOutputTokens { get; init; } = 128;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan DelayBetweenLevels { get; init; } = TimeSpan.FromSeconds(2);
    public string OutputDirectory { get; init; } = "benchmark-results";
    public string Prompt { get; init; } = DefaultSyntheticPrompt;
    public string PromptLabel { get; init; } = "synthetic-coding-v1";
    public string? ApiKeyEnvironmentVariable { get; init; }

    public static string HelpText => """
LlmProxy benchmark harness

Required:
  --target <url>                 Gateway/vLLM service root, for example http://localhost:8080
  --model <name>                 Logical model for LlmProxy or provider model for direct vLLM

Optional:
  --surface chat|responses       Default: chat
  --stream true|false            Default: true
  --concurrency 1,2,4,8          Default: 1,2,4
  --requests <n>                 Requests per concurrency level. Default: 10
  --warmup <n>                   Warm-up requests before the sweep. Default: 2
  --max-output-tokens <n>        Default: 128
  --timeout-seconds <n>          Per-request timeout. Default: 120
  --level-delay-seconds <n>      Delay between concurrency levels. Default: 2
  --prompt-file <path>           Optional custom prompt file; contents are never written to reports
  --prompt-label <label>         Safe report label. Default: synthetic-coding-v1/custom
  --api-key-env <ENV_NAME>       Read bearer token from this environment variable
  --output-dir <path>            Default: benchmark-results
  --help                         Show this help

The harness never writes the prompt body or bearer token to JSON/CSV output.
""";

    public static BenchmarkOptions Parse(IReadOnlyList<string> args)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--target", "--model", "--surface", "--stream", "--concurrency", "--requests", "--warmup",
            "--max-output-tokens", "--timeout-seconds", "--level-delay-seconds", "--prompt-file", "--prompt-label",
            "--api-key-env", "--output-dir"
        };

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var key = args[index];
            if (!known.Contains(key))
            {
                throw new ArgumentException($"Unknown option '{key}'. Use --help for supported options.");
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{key}' requires a value.");
            }

            values[key] = args[++index];
        }

        var targetText = Require(values, "--target");
        if (!Uri.TryCreate(targetText, UriKind.Absolute, out var target) ||
            !(target.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("--target must be an absolute HTTP(S) service-root URL.");
        }

        if (!string.IsNullOrEmpty(target.UserInfo) || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment))
        {
            throw new ArgumentException("--target must not contain embedded credentials, a query string or a fragment.");
        }

        var model = Require(values, "--model").Trim();
        if (model.Length == 0)
        {
            throw new ArgumentException("--model cannot be empty.");
        }

        var surface = Get(values, "--surface", "chat").ToLowerInvariant() switch
        {
            "chat" or "chat-completions" => BenchmarkSurface.ChatCompletions,
            "responses" => BenchmarkSurface.Responses,
            _ => throw new ArgumentException("--surface must be 'chat' or 'responses'.")
        };

        var streaming = ParseBool(Get(values, "--stream", "true"), "--stream");
        var concurrency = ParseConcurrency(Get(values, "--concurrency", "1,2,4"));
        var requests = ParseInt(Get(values, "--requests", "10"), "--requests", 1, 10_000);
        var warmup = ParseInt(Get(values, "--warmup", "2"), "--warmup", 0, 1_000);
        var maxOutputTokens = ParseInt(Get(values, "--max-output-tokens", "128"), "--max-output-tokens", 1, 65_536);
        var timeoutSeconds = ParseInt(Get(values, "--timeout-seconds", "120"), "--timeout-seconds", 1, 3_600);
        var delaySeconds = ParseInt(Get(values, "--level-delay-seconds", "2"), "--level-delay-seconds", 0, 300);
        var outputDirectory = Get(values, "--output-dir", "benchmark-results").Trim();
        if (outputDirectory.Length == 0)
        {
            throw new ArgumentException("--output-dir cannot be empty.");
        }

        var promptFile = values.GetValueOrDefault("--prompt-file");
        var prompt = string.IsNullOrWhiteSpace(promptFile)
            ? DefaultSyntheticPrompt
            : File.ReadAllText(Path.GetFullPath(promptFile)).Trim();
        if (prompt.Length == 0)
        {
            throw new ArgumentException("The benchmark prompt cannot be empty.");
        }

        var promptLabel = values.GetValueOrDefault("--prompt-label")?.Trim();
        if (string.IsNullOrWhiteSpace(promptLabel))
        {
            promptLabel = string.IsNullOrWhiteSpace(promptFile) ? "synthetic-coding-v1" : "custom";
        }

        var apiKeyEnvironmentVariable = values.GetValueOrDefault("--api-key-env")?.Trim();
        if (apiKeyEnvironmentVariable?.Length == 0)
        {
            apiKeyEnvironmentVariable = null;
        }

        return new BenchmarkOptions
        {
            Target = target,
            Model = model,
            Surface = surface,
            Streaming = streaming,
            ConcurrencyLevels = concurrency,
            RequestsPerLevel = requests,
            WarmupRequests = warmup,
            MaxOutputTokens = maxOutputTokens,
            RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            DelayBetweenLevels = TimeSpan.FromSeconds(delaySeconds),
            OutputDirectory = outputDirectory,
            Prompt = prompt,
            PromptLabel = promptLabel,
            ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable
        };
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required option '{key}' is missing.");

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static bool ParseBool(string value, string key) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"{key} must be true or false.");

    private static int ParseInt(string value, string key, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException($"{key} must be an integer between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static int[] ParseConcurrency(string value)
    {
        var levels = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => ParseInt(item, "--concurrency", 1, 256))
            .Distinct()
            .ToArray();

        return levels.Length > 0
            ? levels
            : throw new ArgumentException("--concurrency must contain at least one positive concurrency level.");
    }
}
