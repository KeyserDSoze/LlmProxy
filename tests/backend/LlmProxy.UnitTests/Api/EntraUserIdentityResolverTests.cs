using System.Security.Claims;
using LlmProxy.Api.Identity;

namespace LlmProxy.UnitTests.Api;

public sealed class EntraUserIdentityResolverTests
{
    [Fact]
    public void Resolves_stable_identity_and_roles_from_entra_claims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tid", "tenant-1"),
            new Claim("oid", "object-1"),
            new Claim("preferred_username", "user@example.com"),
            new Claim("name", "Example User"),
            new Claim("roles", "LlmProxy.User")
        ], authenticationType: "test"));

        var resolved = EntraUserIdentityResolver.TryResolve(principal, out var identity);

        Assert.True(resolved);
        Assert.Equal("tenant-1", identity.TenantId);
        Assert.Equal("object-1", identity.ObjectId);
        Assert.Equal("user@example.com", identity.PrincipalName);
        Assert.Equal("Example User", identity.DisplayName);
        Assert.Contains("LlmProxy.User", identity.Roles);
        Assert.Equal("entra:tenant-1/object-1", identity.AuditActor);
    }

    [Fact]
    public void Rejects_authenticated_identity_without_object_id()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tid", "tenant-1"),
            new Claim("preferred_username", "user@example.com")
        ], authenticationType: "test"));

        Assert.False(EntraUserIdentityResolver.TryResolve(principal, out _));
    }
}
