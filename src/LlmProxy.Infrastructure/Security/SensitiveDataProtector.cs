using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace LlmProxy.Infrastructure.Security;

public sealed class SensitiveDataProtector
{
    private const string Prefix = "v1.";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private readonly byte[] _key;

    public SensitiveDataProtector(IConfiguration configuration, IHostEnvironment environment)
    {
        var pepper = configuration["Authentication:ApiKeyPepper"];
        if (string.IsNullOrWhiteSpace(pepper))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException("Authentication:ApiKeyPepper is required outside Development.");
            }

            pepper = "llmproxy-development-pepper-change-me";
        }

        _key = SHA256.HashData(Encoding.UTF8.GetBytes($"llmproxy-sensitive-data-v1|{pepper}"));
    }

    public string Protect(string value, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var plaintext = Encoding.UTF8.GetBytes(value ?? string.Empty);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        var associatedData = Encoding.UTF8.GetBytes(purpose);

        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            var payload = new byte[NonceBytes + TagBytes + ciphertext.Length];
            nonce.CopyTo(payload, 0);
            tag.CopyTo(payload, NonceBytes);
            ciphertext.CopyTo(payload, NonceBytes + TagBytes);
            return Prefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (string.IsNullOrWhiteSpace(protectedValue) || !protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unsupported sensitive-data ciphertext.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Invalid sensitive-data ciphertext.", exception);
        }

        if (payload.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Invalid sensitive-data ciphertext.");
        }

        var nonce = payload.AsSpan(0, NonceBytes);
        var tag = payload.AsSpan(NonceBytes, TagBytes);
        var ciphertext = payload.AsSpan(NonceBytes + TagBytes);
        var plaintext = new byte[ciphertext.Length];
        var associatedData = Encoding.UTF8.GetBytes(purpose);

        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }
}
