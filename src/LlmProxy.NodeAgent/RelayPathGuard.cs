namespace LlmProxy.NodeAgent;

/// <summary>
/// The relay allow-list is checked on the raw path, but the request is later built through <see cref="Uri"/>,
/// which resolves percent-encoded dot segments: "/runtime/18000/v1/%2e%2e/%2e%2e/metrics" becomes "/metrics".
/// Refusing encoded dots, slashes and backslashes keeps the check and the final target the same path.
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
