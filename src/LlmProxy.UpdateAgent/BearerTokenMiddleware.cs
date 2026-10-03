using System.Security.Cryptography;
using System.Text;

namespace LlmProxy.UpdateAgent;

public sealed class BearerTokenMiddleware(RequestDelegate next, UpdateAgentOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var configured = options.BearerToken;
        if (string.IsNullOrWhiteSpace(configured))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "update_agent_not_configured" });
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : string.Empty;

        if (!FixedTimeEquals(configured, supplied))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var left = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(supplied);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
