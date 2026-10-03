using System.Security.Cryptography;
using System.Text;

namespace LlmProxy.NodeAgent;

public sealed class BearerTokenMiddleware(RequestDelegate next, NodeAgentOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(options.BearerToken))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !SecureEquals(header[prefix.Length..], options.BearerToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    private static bool SecureEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return leftBytes.Length == rightBytes.Length &&
                CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
