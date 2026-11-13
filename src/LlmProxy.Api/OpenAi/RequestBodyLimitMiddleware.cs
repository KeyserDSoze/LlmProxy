using Microsoft.AspNetCore.Http.Features;

namespace LlmProxy.Api.OpenAi;

/// <summary>
/// Caps the JSON body of authenticated inference calls. Several stages (content log, token budget, forwarding) each
/// materialise the body as text, so an unbounded body multiplies memory per request. The default is generous for coding
/// contexts and far below Kestrel's 30 MB default; multimodal uploads keep their own, larger bounds.
/// </summary>
public sealed class RequestBodyLimitMiddleware(RequestDelegate next, RequestBodyLimit limit)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!Applies(context.Request))
        {
            await next(context);
            return;
        }

        if (context.Request.ContentLength is { } declared && declared > limit.MaxBytes)
        {
            await RejectAsync(context);
            return;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit.MaxBytes;

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            await RejectAsync(context);
        }
    }

    internal static bool Applies(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
         request.Path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase) ||
         request.Path.StartsWithSegments("/v1/systemone", StringComparison.OrdinalIgnoreCase));

    private async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = $"The request body is larger than the {limit.MaxBytes} bytes this gateway accepts. Send a shorter conversation.",
                type = "invalid_request_error",
                code = "request_too_large"
            }
        });
    }
}

/// <summary>Maximum JSON body accepted on inference routes (<c>Gateway:MaxRequestBodyBytes</c>).</summary>
public sealed record RequestBodyLimit(long MaxBytes)
{
    public const string Key = "Gateway:MaxRequestBodyBytes";
    public const long Default = 16L * 1024 * 1024;
    public const long Minimum = 64L * 1024;
    public const long Maximum = 256L * 1024 * 1024;

    public static RequestBodyLimit From(IConfiguration configuration) => new(Read(configuration[Key]));

    internal static long Read(string? raw) =>
        long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes) &&
        bytes is >= Minimum and <= Maximum
            ? bytes
            : Default;
}
