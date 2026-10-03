using System.Security.Claims;
using LlmProxy.Api.Identity;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.UnitTests.Api;

public sealed class PlatformUserAccessAuthorizationHandlerTests
{
    [Fact]
    public async Task Automatic_mode_registers_authenticated_user_and_allows_self_service()
    {
        await using var dbContext = CreateDbContext();
        dbContext.UserAccessSettings.Add(new UserAccessSettingsRecord
        {
            Id = UserAccessSettingsRecord.SingletonId,
            ProvisioningMode = UserAccessSettingsRecord.AutomaticMode
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var httpContext = new DefaultHttpContext();
        var principal = CreateUser("tenant-1", "object-1", "user@example.com");
        httpContext.User = principal;

        var handler = new PlatformUserAccessAuthorizationHandler(
            dbContext,
            new HttpContextAccessor { HttpContext = httpContext });
        var requirement = new PlatformUserAccessRequirement();
        var authorization = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(authorization);

        Assert.True(authorization.HasSucceeded);
        var user = await dbContext.PlatformUsers.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tenant-1", user.TenantId);
        Assert.Equal("object-1", user.ObjectId);
        Assert.Equal("user@example.com", user.PrincipalName);
        Assert.Equal("automatic", user.ProvisioningSource);
        Assert.True(user.Enabled);
        Assert.NotNull(user.LastSeenAtUtc);
        Assert.Contains(dbContext.AuditEvents.Local, item => item.Action == "user.auto_provision");
    }

    [Fact]
    public async Task Manual_mode_denies_authenticated_user_that_is_not_registered()
    {
        await using var dbContext = CreateDbContext();
        dbContext.UserAccessSettings.Add(new UserAccessSettingsRecord
        {
            Id = UserAccessSettingsRecord.SingletonId,
            ProvisioningMode = UserAccessSettingsRecord.ManualMode
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var principal = CreateUser("tenant-1", "object-missing", "missing@example.com");
        var httpContext = new DefaultHttpContext { User = principal };
        var handler = new PlatformUserAccessAuthorizationHandler(
            dbContext,
            new HttpContextAccessor { HttpContext = httpContext });
        var requirement = new PlatformUserAccessRequirement();
        var authorization = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(authorization);

        Assert.False(authorization.HasSucceeded);
        Assert.Empty(dbContext.PlatformUsers);
    }

    [Fact]
    public async Task Disabled_registered_user_is_denied_in_both_modes()
    {
        await using var dbContext = CreateDbContext();
        dbContext.UserAccessSettings.Add(new UserAccessSettingsRecord
        {
            Id = UserAccessSettingsRecord.SingletonId,
            ProvisioningMode = UserAccessSettingsRecord.AutomaticMode
        });
        dbContext.PlatformUsers.Add(new PlatformUserRecord
        {
            TenantId = "tenant-1",
            ObjectId = "object-disabled",
            PrincipalName = "disabled@example.com",
            Enabled = false,
            ProvisioningSource = "admin",
            DisabledAtUtc = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var principal = CreateUser("tenant-1", "object-disabled", "disabled@example.com");
        var httpContext = new DefaultHttpContext { User = principal };
        var handler = new PlatformUserAccessAuthorizationHandler(
            dbContext,
            new HttpContextAccessor { HttpContext = httpContext });
        var requirement = new PlatformUserAccessRequirement();
        var authorization = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(authorization);

        Assert.False(authorization.HasSucceeded);
    }

    [Fact]
    public async Task Administrator_bypasses_normal_user_registry()
    {
        await using var dbContext = CreateDbContext();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, ConfiguredSuperAdminClaimsTransformation.AdminRole)
        ], authenticationType: "test"));

        var httpContext = new DefaultHttpContext { User = principal };
        var handler = new PlatformUserAccessAuthorizationHandler(
            dbContext,
            new HttpContextAccessor { HttpContext = httpContext });
        var requirement = new PlatformUserAccessRequirement();
        var authorization = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await handler.HandleAsync(authorization);

        Assert.True(authorization.HasSucceeded);
    }

    private static GatewayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GatewayDbContext(options);
    }

    private static ClaimsPrincipal CreateUser(string tenantId, string objectId, string principalName)
        => new(new ClaimsIdentity(
        [
            new Claim("tid", tenantId),
            new Claim("oid", objectId),
            new Claim("preferred_username", principalName),
            new Claim("name", "Example User")
        ], authenticationType: "test"));
}
