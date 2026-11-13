namespace LlmProxy.Api.Security;

/// <summary>
/// ASP.NET Core routing treats "/v1/chat/completions/" as "/v1/chat/completions", but the governance
/// middlewares (output-token budget, content log) compare the raw path. Canonicalising the trailing slash
/// once, before everything else, keeps every layer looking at the same path.
/// </summary>
public sealed class InferencePathNormalizationMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Request.Path = Canonicalize(context.Request.Path);
        return next(context);
    }

    internal static PathString Canonicalize(PathString path)
    {
        var value = path.Value;
        if (value is null || value.Length <= 1 || value[^1] != '/' ||
            !path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return new PathString(value.TrimEnd('/'));
    }
}
