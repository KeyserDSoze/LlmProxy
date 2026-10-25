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
        return best;
    }

    public static string ForUi(string rawLog, string? persistedFile)
    {
        var scrubbed = Sanitize(rawLog);
        var root = FindRootCause(scrubbed);
        var lines = scrubbed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join("\n", lines.TakeLast(7));
        if (tail.Length > 650) tail = tail[^650..];
        var summary = root is null
            ? "Docker logs did not contain an identifiable root exception."
            : "Likely root cause: " + root;
        return "\n" + summary +
               (persistedFile is null ? "" : "\nFull bounded startup log saved on the inference host: " + persistedFile) +
               (tail.Length == 0 ? "" : "\nLast Docker log lines:\n" + tail);
    }
}
