namespace LlmProxy.Api.AgentConnectivity;

/// <summary>
/// Same rule as the Node Agent: a percent-encoded dot or slash would be resolved by <see cref="Uri"/> after the
/// allow-list ran, so the request would reach a different path than the one that was approved.
/// </summary>
public static class RelayPathGuard
{
    private static readonly string[] Forbidden = ["..", "%2e", "%2f", "%5c", "%00", "\\"];

    public static bool IsSafe(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 8192 || !path.StartsWith('/')) return false;
        if (path.Any(char.IsControl)) return false;
        return !Forbidden.Any(fragment => path.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
