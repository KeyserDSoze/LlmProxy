using LlmProxy.Domain.Governance;

namespace LlmProxy.UnitTests.Domain;

public sealed class UserRateLimitPolicyTests
{
    [Fact]
    public void User_rate_limit_normalizes_model_and_preserves_owner()
    {
        var policy = new UserRateLimitPolicy(
            "tenant-1",
            "user-1",
            "  agic-code  ",
            100,
            60,
            true);

        Assert.Equal("tenant-1", policy.OwnerTenantId);
        Assert.Equal("user-1", policy.OwnerObjectId);
        Assert.Equal("agic-code", policy.LogicalModel);
        Assert.Equal(100, policy.RequestsPerWindow);
        Assert.Equal(60, policy.WindowSeconds);
        Assert.True(policy.Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void User_rate_limit_requires_stable_owner_ids(string value)
    {
        Assert.Throws<ArgumentException>(() => new UserRateLimitPolicy(value, "user-1", null, 10));
        Assert.Throws<ArgumentException>(() => new UserRateLimitPolicy("tenant-1", value, null, 10));
    }
}
