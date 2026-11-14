using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

/// <summary>
/// Asks a container registry for the digest a tag points to, without downloading the image. This is the same HEAD request
/// Docker itself makes, so it needs no extra Docker plugin. Public repositories only (anonymous token); a private registry
/// simply reports "unknown".
/// </summary>
public static partial class RegistryDigestClient
{
    public sealed record ImageReference(string Registry, string Repository, string Tag);

    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json"
    ];

    public static ImageReference? Parse(string image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Contains('@')) return null;
        var slash = image.IndexOf('/');
        var first = slash < 0 ? null : image[..slash];
        string registry, rest;
        if (first is not null && (first.Contains('.') || first.Contains(':') || first == "localhost")) { registry = first; rest = image[(slash + 1)..]; }
        else { registry = "registry-1.docker.io"; rest = slash < 0 ? "library/" + image : image; }
        var tag = "latest";
        var colon = rest.LastIndexOf(':');
        if (colon > rest.LastIndexOf('/')) { tag = rest[(colon + 1)..]; rest = rest[..colon]; }
        if (rest.Length == 0 || tag.Length == 0) return null;
        return new ImageReference(registry, rest, tag);
    }

    /// <summary>The realm, service and scope of a <c>WWW-Authenticate: Bearer</c> challenge.</summary>
    public static IReadOnlyDictionary<string, string> ParseChallenge(string header)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return values;
        foreach (Match match in ChallengePart().Matches(header["Bearer ".Length..]))
            values[match.Groups["key"].Value] = match.Groups["value"].Value;
        return values;
    }

    public static async Task<string?> GetDigestAsync(HttpClient client, string image, CancellationToken token)
    {
        if (Parse(image) is not { } reference) return null;
        var url = $"https://{reference.Registry}/v2/{reference.Repository}/manifests/{reference.Tag}";
        var response = await HeadAsync(client, url, null, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            response.Headers.WwwAuthenticate.FirstOrDefault() is { } challenge &&
            ParseChallenge(challenge.ToString()) is { } parts && parts.TryGetValue("realm", out var realm) &&
            Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) && realmUri.Scheme == Uri.UriSchemeHttps)
        {
            var query = string.Join('&', new[] { "service", "scope" }.Where(parts.ContainsKey).Select(key => $"{key}={Uri.EscapeDataString(parts[key])}"));
            using var tokenResponse = await client.GetAsync(realm + (realm.Contains('?') ? "&" : "?") + query, token);
            if (!tokenResponse.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(token));
            var bearer = document.RootElement.TryGetProperty("token", out var t) ? t.GetString()
                : document.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(bearer)) return null;
            response.Dispose();
            response = await HeadAsync(client, url, bearer, token);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            return response.Headers.TryGetValues("Docker-Content-Digest", out var digest) ? digest.FirstOrDefault() : null;
        }
    }

    private static Task<HttpResponseMessage> HeadAsync(HttpClient client, string url, string? bearer, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request, token);
    }

    [GeneratedRegex("(?<key>[A-Za-z_]+)=\"(?<value>[^\"]*)\"")]
    private static partial Regex ChallengePart();
}
