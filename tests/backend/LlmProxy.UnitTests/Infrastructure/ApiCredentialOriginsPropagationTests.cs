using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class ApiCredentialOriginsPropagationTests
{
    [Fact]
    public async Task Changing_only_the_allowed_origins_refreshes_the_runtime_credential_cache()
    {
        var cache = new InMemoryApiCredentialCache();
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase("origins-" + Guid.NewGuid())
            .AddInterceptors(new ApiCredentialCacheSaveChangesInterceptor(cache))
            .Options;
        await using var db = new GatewayDbContext(options);
        var credential = new ApiCredential("web app", "lp_org_abcd", "hash-1");
        credential.SetAllowedOrigins(["https://a.example"]);
        db.ApiCredentials.Add(credential);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(cache.AnyAllowsOrigin("https://a.example"));

        credential.SetAllowedOrigins(["https://b.example"]);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(cache.AnyAllowsOrigin("https://a.example"));
        Assert.True(cache.AnyAllowsOrigin("https://b.example"));
        Assert.True(cache.TryGetUsableByHash("hash-1", DateTimeOffset.UtcNow, out var snapshot));
        Assert.Equal(new[] { "https://b.example" }, snapshot.AllowedOrigins);
    }
}
