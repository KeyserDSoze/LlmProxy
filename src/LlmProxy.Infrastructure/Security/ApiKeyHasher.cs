using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace LlmProxy.Infrastructure.Security;

public sealed class ApiKeyHasher
{
    private readonly byte[] _pepper;

    public ApiKeyHasher(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPepper = configuration["Authentication:ApiKeyPepper"];
        if (string.IsNullOrWhiteSpace(configuredPepper))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Authentication:ApiKeyPepper is required outside Development.");
            }

            configuredPepper = "llmproxy-development-pepper-change-me";
        }

        _pepper = Encoding.UTF8.GetBytes(configuredPepper);
    }

    public string Hash(string secret)
    {
        using var hmac = new HMACSHA256(_pepper);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(secret)));
    }

    public static string GenerateSecret(string prefix = "lp_")
        => $"{prefix}{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

    public static string GetPrefix(string secret)
        => secret[..Math.Min(secret.Length, 15)];
}
