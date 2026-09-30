using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Security;

public sealed class UpstreamCredentialProtector
{
    private const string Prefix = "v1.";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxSecretUtf8Bytes = 2048;
    private readonly byte[]? _key;

    public UpstreamCredentialProtector(IConfiguration configuration)
    {
        var raw = configuration["Security:UpstreamCredentialEncryptionKey"];
        if (string.IsNullOrWhiteSpace(raw)) return;
        try { _key = Convert.FromHexString(raw.Trim()); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Security:UpstreamCredentialEncryptionKey must be a 64-character hex value (32 bytes).", exception);
        }
        if (_key.Length != KeyBytes)
            throw new InvalidOperationException("Security:UpstreamCredentialEncryptionKey must be exactly 32 bytes (64 hex characters).");
    }

    public bool IsConfigured => _key is { Length: KeyBytes };

    public string Protect(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Upstream bearer token is required.", nameof(secret));
        var key = RequireKey();
        var plaintext = Encoding.UTF8.GetBytes(secret);
        if (plaintext.Length > MaxSecretUtf8Bytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new ArgumentException($"Upstream bearer token must be at most {MaxSecretUtf8Bytes} UTF-8 bytes.", nameof(secret));
        }
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            var payload = new byte[NonceBytes + TagBytes + ciphertext.Length];
            nonce.CopyTo(payload, 0); tag.CopyTo(payload, NonceBytes); ciphertext.CopyTo(payload, NonceBytes + TagBytes);
            return Prefix + Convert.ToBase64String(payload);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret) || !protectedSecret.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unsupported upstream credential ciphertext.");
        var key = RequireKey();
        byte[] payload;
        try { payload = Convert.FromBase64String(protectedSecret[Prefix.Length..]); }
        catch (FormatException exception) { throw new CryptographicException("Invalid upstream credential ciphertext.", exception); }
        if (payload.Length < NonceBytes + TagBytes + 1) throw new CryptographicException("Invalid upstream credential ciphertext.");
        var nonce = payload.AsSpan(0, NonceBytes);
        var tag = payload.AsSpan(NonceBytes, TagBytes);
        var ciphertext = payload.AsSpan(NonceBytes + TagBytes);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void ApplyBearer(HttpRequestMessage request, string? protectedSecret)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(protectedSecret)) return;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Unprotect(protectedSecret));
    }

    private byte[] RequireKey() => _key ?? throw new InvalidOperationException(
        "Security:UpstreamCredentialEncryptionKey is required to store or use upstream provider credentials.");
}
