using System.Security.Cryptography;
using System.Text;

namespace LlmProxy.Api.Security;

public sealed class InferenceApiKeyMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        var expected = configuration["Authentication:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            if (environment.IsDevelopment())
            {
                await next(context);
                return;
            }

            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "gateway_not_configured", "Inference authentication is not configured.");
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out var authorization) ||
            !authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "A bearer API key is required.");
            return;
        }

        var supplied = authorization.ToString()["Bearer ".Length..].Trim();
        if (!FixedTimeEquals(supplied, expected))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_api_key", "The supplied API key is invalid.");
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type = "authentication_error",
                code
            }
        });
    }
}
