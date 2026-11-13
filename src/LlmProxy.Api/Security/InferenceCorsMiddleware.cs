using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Security;
using LlmProxy.Domain.Security;

namespace LlmProxy.Api.Security;

/// <summary>
/// Browser access (CORS) for the inference API. A preflight carries no API key, so it is answered when the
/// origin is allowed site-wide or by any enabled key; the key-specific check runs with the actual request
/// in <see cref="InferenceApiKeyMiddleware"/>. Requests without an Origin header are not affected.
/// </summary>
public sealed class InferenceCorsMiddleware(RequestDelegate next)
{
    public const string CredentialOriginsItem = "LlmProxy.CredentialAllowedOrigins";
    private const string AllowedMethods = "GET, POST, OPTIONS";
    private const string ExposedHeaders = "X-LlmProxy-Request-Id, X-LlmProxy-Trace-Id, Retry-After";
    private const string DefaultAllowedHeaders = "authorization, content-type";

    public async Task InvokeAsync(HttpContext context, CorsPolicyState policy, IApiCredentialCache credentials)
    {
        if (!context.Request.Path.StartsWithSegments("/v1") ||
            !context.Request.Headers.TryGetValue("Origin", out var originHeader))
        {
            await next(context);
            return;
        }

        var valid = OriginPolicy.TryNormalize(originHeader.ToString(), out var origin) && origin != OriginPolicy.Wildcard;

        if (HttpMethods.IsOptions(context.Request.Method) &&
            context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            await AnswerPreflightAsync(context, policy, credentials, valid, origin);
            return;
        }

        context.Response.OnStarting(() =>
        {
            // The credential is known by now unless authentication itself failed.
            var known = context.Items.ContainsKey(InferenceApiKeyMiddleware.ApiCredentialIdItem);
            var credentialOrigins = context.Items.TryGetValue(CredentialOriginsItem, out var value)
                ? value as IReadOnlyList<string> : null;
            if (valid && (policy.Allows(origin, credentialOrigins) || (!known && credentials.AnyAllowsOrigin(origin))))
            {
                ApplyAllowHeaders(context.Response, origin);
                context.Response.Headers["Access-Control-Expose-Headers"] = ExposedHeaders;
            }

            return Task.CompletedTask;
        });

        await next(context);
    }

    private static async Task AnswerPreflightAsync(
        HttpContext context, CorsPolicyState policy, IApiCredentialCache credentials, bool valid, string origin)
    {
        if (!valid || !(policy.GlobalAllows(origin) || credentials.AnyAllowsOrigin(origin)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "This origin is not allowed to call the API from a browser.",
                    type = "invalid_request_error",
                    code = "origin_not_allowed"
                }
            });
            return;
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status204NoContent;
        ApplyAllowHeaders(response, origin);
        response.Headers["Access-Control-Allow-Methods"] = AllowedMethods;
        response.Headers["Access-Control-Allow-Headers"] = SafeRequestedHeaders(context.Request);
        response.Headers["Access-Control-Max-Age"] = policy.Current.MaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ApplyAllowHeaders(HttpResponse response, string origin)
    {
        response.Headers["Access-Control-Allow-Origin"] = origin;
        response.Headers.Append("Vary", "Origin");
    }

    // Echo only well-formed header names so the response cannot be used to inject arbitrary text.
    private static string SafeRequestedHeaders(HttpRequest request)
    {
        var requested = request.Headers["Access-Control-Request-Headers"].ToString();
        if (requested.Length is 0 or > 512) return DefaultAllowedHeaders;
        foreach (var part in requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 64 || part.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-'))) return DefaultAllowedHeaders;
        }

        return requested;
    }
}
