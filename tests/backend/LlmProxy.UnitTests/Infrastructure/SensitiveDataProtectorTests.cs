using System.Security.Cryptography;
using LlmProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class SensitiveDataProtectorTests
{
    [Fact]
    public void Protect_round_trips_with_purpose_binding()
    {
        var protector = Create("pepper-a");
        var ciphertext = protector.Protect("lp_secret_value", "api-credential:123");

        Assert.StartsWith("v1.", ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain("lp_secret_value", ciphertext, StringComparison.Ordinal);
        Assert.Equal("lp_secret_value", protector.Unprotect(ciphertext, "api-credential:123"));
    }

    [Fact]
    public void Wrong_purpose_cannot_decrypt_ciphertext()
    {
        var protector = Create("pepper-a");
        var ciphertext = protector.Protect("{\"prompt\":\"hello\"}", "content-log:req-1:request");

        Assert.ThrowsAny<CryptographicException>(() =>
            protector.Unprotect(ciphertext, "content-log:req-1:response"));
    }

    [Fact]
    public void Wrong_pepper_cannot_decrypt_ciphertext()
    {
        var ciphertext = Create("pepper-a").Protect("sensitive", "purpose");

        Assert.ThrowsAny<CryptographicException>(() =>
            Create("pepper-b").Unprotect(ciphertext, "purpose"));
    }

    [Fact]
    public void Missing_pepper_is_rejected_outside_development()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() =>
            new SensitiveDataProtector(configuration, new FakeHostEnvironment(Environments.Production)));
    }

    private static SensitiveDataProtector Create(string pepper)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ApiKeyPepper"] = pepper
            })
            .Build();

        return new SensitiveDataProtector(configuration, new FakeHostEnvironment(Environments.Production));
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "LlmProxy.UnitTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
