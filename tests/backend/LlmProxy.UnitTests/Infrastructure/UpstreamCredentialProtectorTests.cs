using System.Security.Cryptography;
using LlmProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class UpstreamCredentialProtectorTests
{
    private const string Key = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    [Fact]
    public void Protect_round_trips_without_exposing_plaintext()
    {
        var protector = Create(Key);
        var ciphertext = protector.Protect("llama-local");
        Assert.StartsWith("v1.", ciphertext);
        Assert.DoesNotContain("llama-local", ciphertext, StringComparison.Ordinal);
        Assert.Equal("llama-local", protector.Unprotect(ciphertext));
    }

    [Fact]
    public void Wrong_key_cannot_decrypt_ciphertext()
    {
        var ciphertext = Create(Key).Protect("llama-local");
        var other = Create("ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100");
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(ciphertext));
    }

    [Fact]
    public void Missing_key_allows_requests_without_upstream_auth_but_refuses_protection()
    {
        var protector = Create(null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://example.test");
        protector.ApplyBearer(request, null);
        Assert.False(protector.IsConfigured);
        Assert.Null(request.Headers.Authorization);
        Assert.Throws<InvalidOperationException>(() => protector.Protect("secret"));
    }

    private static UpstreamCredentialProtector Create(string? key)
    {
        var values = new Dictionary<string, string?>();
        if (key is not null) values["Security:UpstreamCredentialEncryptionKey"] = key;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new UpstreamCredentialProtector(configuration);
    }
}
