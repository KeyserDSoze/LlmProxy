using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed record DockerBuildDiagnosis(bool TransientNetworkFailure, string Message);

/// <summary>Keep the actual final failure, not the first 500 chars of pip's dependency chatter.</summary>
public static class DockerBuildDiagnostics
{
    private static readonly Regex NetworkFailure = new(
        @"(?i)(connection (?:reset|refused|aborted|broken|closed)|connectionerror|readtimeout|connecttimeout|timed? out|temporary failure in name resolution|name or service not known|tls handshake timeout|http(?:/\d(?:\.\d)?)? (?:429|500|502|503|504)|unexpected eof|network is unreachable|remote end closed|chunkedencodingerror)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RootCause = new(
        @"(?i)(error:|failed to solve|exception:|could not|no space left|not found|no matching distribution|timeout|connection reset|connectionerror|readtimeout|tls|temporary failure|network is unreachable|http.? 429|unexpected eof|exit code)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DockerBuildDiagnosis Analyze(CommandResult result)
    {
        var combined = result.StandardError + "\n" + result.StandardOutput;
        var lines = combined.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0).ToArray();
        var relevant = lines.Where(x => RootCause.IsMatch(x)).TakeLast(4).ToArray();
        var excerpt = relevant.Length > 0 ? string.Join(" | ", relevant)
            : string.Join(" | ", lines.TakeLast(3));
        if (excerpt.Length > 1000) excerpt = excerpt[^1000..];
        var transient = NetworkFailure.IsMatch(combined);
        var label = transient ? "Possible transient network/PyPI interruption" : "Runtime build or download failed";
        return new DockerBuildDiagnosis(transient,
            $"{label} (Docker exit {result.ExitCode}). {excerpt}".Trim());
    }
}
