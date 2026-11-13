namespace LlmProxy.Api.Admin;

/// <summary>
/// Cloudflare replaces an origin 502 or 504 with its own HTML page, which hid the real reason from the operator
/// ("HTTP 502: the server returned an HTML error page"). Failures of an Agent or another dependency are therefore
/// reported to the Admin app as 424 (Failed Dependency); the body, and the original status in a header, are unchanged.
/// Public <c>/v1</c> routes keep their statuses because API clients rely on them for retries.
/// </summary>
public sealed class AdminErrorStatusMiddleware(RequestDelegate next)
{
    public const string OriginalStatusHeader = "X-LlmProxy-Original-Status";

    public Task InvokeAsync(HttpContext context)
    {
        if (Applies(context.Request.Path))
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode is StatusCodes.Status502BadGateway or StatusCodes.Status504GatewayTimeout)
                {
                    context.Response.Headers[OriginalStatusHeader] = context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    context.Response.StatusCode = StatusCodes.Status424FailedDependency;
                }
                return Task.CompletedTask;
            });
        }
        return next(context);
    }

    internal static bool Applies(PathString path) =>
        path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/api/me", StringComparison.OrdinalIgnoreCase);
}
