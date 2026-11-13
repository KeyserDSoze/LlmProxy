using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

/// <summary>
/// Extract the FIRST meaningful vLLM/driver exception from startup output.
/// Final "Engine core initialization failed" tracebacks are downstream
/// symptoms; the original exception is normally earlier in the log.
/// </summary>
public static class DockerStartupDiagnostics
{
    private static readonly Regex Secrets = new(
        @"(?i)(hf_[a-z0-9]{12,}|(?<=Bearer\s)[a-z0-9._-]{12,}|((?:token|password|secret|api[_-]?key)\s*[=:]\s*)[^\s,'"";}]{6,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*[A-Za-z]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PrimaryError = new(
        @"(?i)(?:ValueError|OutOfMemoryError|MemoryError|RuntimeError|ImportError|ModuleNotFoundError|NotImplementedError|AssertionError|OSError|TypeError|CUDA error|CUDA out of memory|No available memory for the cache blocks|torch\.cuda\.OutOfMemoryError)\s*[:(]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] GenericFailures =
    [
        "Engine core initialization failed",
        "EngineCore failed to start",
        "Failed core proc(s)",
        "worker exited unexpectedly"
    ];

    private sealed record KnownCause(Regex Pattern, string Hint);

    // llama.cpp prints the real cause early (download/HF resolution, architecture, memory);
    // "failed to load model ''" at the end is a downstream symptom. Most specific first.
    private static readonly KnownCause[] LlamaCppCauses =
    [
        new(new(@"no GGUF files found in repository", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "The repository or the :QUANT suffix does not match a GGUF file. Pick an available quantization or a -GGUF repository and reinstall."),
        new(new(@"GET failed \((401|403)\)|failed to resolve commit for|invalid token, authentication disabled", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "Hugging Face refused access or the repository could not be resolved: check the repository ID, gated/private access and the Hugging Face access token configured in LlmProxy Admin."),
        new(new(@"GET failed \((404)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "The Hugging Face repository was not found. Check the org/model ID."),
        new(new(@"GET failed \((429|5\d\d)\)|HTTPLIB failed|Download '.*' failed with status code|download failed after \d+ attempts|cannot make GET request", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "The runtime could not download weights at start (network, proxy or Hub rate limit). Start again: the Agent now pre-downloads and pins the GGUF before launching llama.cpp."),
        new(new(@"invalid HF repo format", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "Use the repository format org/model or org/model:QUANT."),
        new(new(@"unknown model architecture|error loading model architecture", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "This llama.cpp image does not support the model architecture yet. The tag is mutable and Docker does not refresh a local copy on its own: run docker pull on the server (ghcr.io/ggml-org/llama.cpp:server-cuda) or set NodeAgent__LlamaCppDockerImage to a newer build, then Start again."),
        new(new(@"argument --(?:tool-call-parser|reasoning-parser|generation-config)[:=]? *(?:\S+ )?(?:invalid choice|expected)|unrecognized arguments:.*--(?:tool-call-parser|reasoning-parser|enable-auto-tool-choice)|invalid choice: '[a-z0-9_]+' \(choose from", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "This inference image does not know a tool-call or reasoning parser of the model-family profile. Use a newer image (see the requirement shown under the model), or set NodeAgent__ApplyModelFamilyProfiles=false on the server and pass the flags you need yourself."),
        new(new(@"cudaMalloc failed|out of memory|failed to allocate .* buffer|unable to allocate", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "Not enough GPU/unified memory: reduce context (ctx-size), parallel slots, choose a smaller quantization, or stop other models."),
        new(new(@"error loading model:|failed to read magic|invalid magic|tensor .* data is not within the file bounds|corrupted", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "The GGUF file could not be read (incomplete, corrupted or not a GGUF). Uninstall and reinstall to download and verify it again."),
        new(new(@"exactly one out metadata, path_model, and file must be defined|failed to load model ''", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "llama.cpp started without a resolved GGUF file (repo/:QUANT not resolvable or downloadable at start). Start again: the Agent now downloads and pins the exact GGUF first and reports the precise cause if it cannot.")
    ];

    public static string? FindHint(string rawLog)
    {
        var text = Sanitize(rawLog);
        return LlamaCppCauses.FirstOrDefault(cause => cause.Pattern.IsMatch(text))?.Hint;
    }

    private static string? FindLlamaCppCause(IReadOnlyList<string> lines, bool includeGeneric)
    {
        foreach (var cause in includeGeneric ? LlamaCppCauses : LlamaCppCauses[..^1])
        {
            var line = lines.FirstOrDefault(item => cause.Pattern.IsMatch(item))?.Trim();
            if (line is not null) return line.Length > 950 ? line[..950] : line;
        }
        return null;
    }

    public static string Sanitize(string text)
    {
        var noControl = Ansi.Replace(text, "");
        noControl = new string(noControl.Where(c => c is '\r' or '\n' or '\t' || !char.IsControl(c)).ToArray());
        return Secrets.Replace(noControl, match =>
        {
            var equal = match.Value.IndexOfAny(['=', ':']);
            return equal >= 0 && !match.Value.StartsWith("hf_", StringComparison.OrdinalIgnoreCase)
                ? match.Value[..(equal + 1)] + "[REDACTED]"
                : "[REDACTED]";
        });
    }

    public static string? FindRootCause(string rawLog)
    {
        var lines = Sanitize(rawLog).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? best = null;
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.Length == 0 || GenericFailures.Any(g =>
                text.Contains(g, StringComparison.OrdinalIgnoreCase))) continue;
            var match = PrimaryError.Match(text);
            if (!match.Success) continue;
            var candidate = text[match.Index..];
            // GPU OOM, missing package and startup argument errors are often
            // followed by generic Python RuntimeErrors.
            if (candidate.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("CUDA", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("ValueError:", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("ImportError:", StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains("ModuleNotFoundError:", StringComparison.OrdinalIgnoreCase))
                return candidate.Length > 950 ? candidate[..950] : candidate;
            best ??= candidate.Length > 950 ? candidate[..950] : candidate;
        }
        return FindLlamaCppCause(lines, includeGeneric: false) ?? best ??
               FindLlamaCppCause(lines, includeGeneric: true);
    }

    public static string ForUi(string rawLog, string? persistedFile)
    {
        var scrubbed = Sanitize(rawLog);
        var root = FindRootCause(scrubbed);
        var hint = root is null ? null : FindHint(scrubbed);
        var lines = scrubbed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join("\n", lines.TakeLast(7));
        if (tail.Length > 650) tail = tail[^650..];
        var summary = root is null
            ? "Docker logs did not contain an identifiable root exception."
            : "Likely root cause: " + root;
        return "\n" + summary +
               (hint is null ? "" : "\nWhat to do: " + hint) +
               (persistedFile is null ? "" : "\nFull bounded startup log saved on the inference host: " + persistedFile) +
               (tail.Length == 0 ? "" : "\nLast Docker log lines:\n" + tail);
    }
}
