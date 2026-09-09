namespace LlmProxy.Domain.Nodes;

/// <summary>
/// Defines the contract for a node service root. The address may contain a host name,
/// localhost, an IPv4/IPv6 address, a port and an optional path prefix.
/// </summary>
public static class InferenceEndpoint
{
    public static string NormalizeBaseAddress(string baseAddress)
    {
        if (string.IsNullOrWhiteSpace(baseAddress) ||
            !Uri.TryCreate(baseAddress.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Node base address must be an absolute HTTP(S) URI.", nameof(baseAddress));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Node base address must not contain embedded credentials.", nameof(baseAddress));
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Node base address must not contain a query string or fragment.", nameof(baseAddress));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static Uri Combine(string baseAddress, string apiPath)
    {
        var normalizedBaseAddress = NormalizeBaseAddress(baseAddress);
        if (string.IsNullOrWhiteSpace(apiPath))
        {
            return new Uri(normalizedBaseAddress, UriKind.Absolute);
        }

        var normalizedPath = apiPath.Trim();
        if (Uri.TryCreate(normalizedPath, UriKind.Absolute, out _))
        {
            throw new ArgumentException("API path must be relative to the node base address.", nameof(apiPath));
        }

        return new Uri($"{normalizedBaseAddress}/{normalizedPath.TrimStart('/')}", UriKind.Absolute);
    }
}
