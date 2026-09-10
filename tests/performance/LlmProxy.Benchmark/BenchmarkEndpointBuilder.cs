namespace LlmProxy.Benchmarking;

public static class BenchmarkEndpointBuilder
{
    public static Uri Build(Uri serviceRoot, BenchmarkSurface surface)
    {
        ArgumentNullException.ThrowIfNull(serviceRoot);

        var root = serviceRoot.AbsoluteUri.TrimEnd('/');
        var suffix = surface switch
        {
            BenchmarkSurface.ChatCompletions => "/chat/completions",
            BenchmarkSurface.Responses => "/responses",
            _ => throw new ArgumentOutOfRangeException(nameof(surface))
        };

        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri(root + suffix, UriKind.Absolute)
            : new Uri(root + "/v1" + suffix, UriKind.Absolute);
    }
}
