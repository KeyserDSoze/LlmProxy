using LlmProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class ApiKeyHasherTests
{
    [Fact]
    public void Hash_is_deterministic_for_same_secret_and_pepper()
    {
        var hasher = CreateHasher("pepper-a", Environments.Production);

        var first = hasher.Hash("lp_secret");
        var second = hasher.Hash("lp_secret");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_peppers_produce_different_hashes()
    {
        var first = CreateHasher("pepper-a", Environments.Production).Hash("lp_secret");
        var second = CreateHasher("pepper-b", Environments.Production).Hash("lp_secret");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Missing_pepper_is_rejected_outside_development()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() =>
            new ApiKeyHasher(configuration, new FakeHostEnvironment(Environments.Production)));
    }

    [Fact]
    public void Generated_secret_uses_llmproxy_prefix()
    {
        var secret = ApiKeyHasher.GenerateSecret();

        Assert.StartsWith("lp_", secret, StringComparison.Ordinal);
        Assert.True(secret.Length > 32);
    }

    private static ApiKeyHasher CreateHasher(string pepper, string environment)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ApiKeyPepper"] = pepper
            })
            .Build();

        return new ApiKeyHasher(configuration, new FakeHostEnvironment(environment));
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "LlmProxy.UnitTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
