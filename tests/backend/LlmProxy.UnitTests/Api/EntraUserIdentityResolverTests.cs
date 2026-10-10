using System.Security.Claims;
using LlmProxy.Api.Identity;
using Microsoft.Extensions.Configuration;

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


    [Fact]
    public async Task Configured_super_admin_email_receives_admin_role()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:SuperAdmins"] = "first@example.com; Admin@Example.com "
            })
            .Build();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("preferred_username", "admin@example.com"),
            new Claim("oid", "object-1")
        ], authenticationType: "test"));

        var transformed = await new ConfiguredSuperAdminClaimsTransformation(configuration)
            .TransformAsync(principal);

        Assert.True(transformed.IsInRole("LlmProxy.Admin"));
    }

    [Fact]
    public async Task Configured_super_admin_comma_and_semicolon_lists_both_match()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:SuperAdmins"] = "first@example.com, second@example.com; third@example.com"
            })
            .Build();
        var claims = new ClaimsIdentity([
            new Claim("preferred_username", "second@example.com"),
            new Claim("oid", "object-2")
        ], authenticationType: "test");
        var principal = new ClaimsPrincipal(claims);

        var transformed = await new ConfiguredSuperAdminClaimsTransformation(configuration)
            .TransformAsync(principal);

        Assert.True(transformed.IsInRole("LlmProxy.Admin"));
    }

    [Fact]
    public async Task Configured_super_admin_matches_mapped_dotnet_name_claim()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:SuperAdmins"] = "admin@example.com"
            })
            .Build();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin@example.com"),
            new Claim("oid", "object-1")
        ], authenticationType: "test"));

        var transformed = await new ConfiguredSuperAdminClaimsTransformation(configuration)
            .TransformAsync(principal);

        Assert.Equal("admin@example.com", principal.Identity!.Name);
        Assert.True(transformed.IsInRole("LlmProxy.Admin"));
    }

    [Fact]
    public async Task Configured_super_admin_object_id_receives_admin_role()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:SuperAdmins"] = "oid:object-42"
            })
            .Build();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("preferred_username", "renamed@example.com"),
            new Claim("oid", "object-42")
        ], authenticationType: "test"));

        var transformed = await new ConfiguredSuperAdminClaimsTransformation(configuration)
            .TransformAsync(principal);

        Assert.True(transformed.IsInRole("LlmProxy.Admin"));
    }

    [Fact]
    public async Task Non_matching_super_admin_configuration_does_not_grant_admin_role()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:SuperAdmins"] = "admin@example.com"
            })
            .Build();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("preferred_username", "other@example.com"),
            new Claim("oid", "object-other")
        ], authenticationType: "test"));

        var transformed = await new ConfiguredSuperAdminClaimsTransformation(configuration)
            .TransformAsync(principal);

        Assert.False(transformed.IsInRole("LlmProxy.Admin"));
    }
}
