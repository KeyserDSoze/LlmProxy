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
    [Fact]
    public void User_quota_supports_output_token_budget()
    {
        var policy = new UserRateLimitPolicy("tenant-1", "user-1", null, 100, 60, true, 50000, 4096);

        Assert.Equal(50000, policy.OutputTokensPerWindow);
        Assert.Equal(4096, policy.MaxOutputTokensPerRequest);

        policy.ClearOutputTokenBudget();

        Assert.Null(policy.OutputTokensPerWindow);
        Assert.Null(policy.MaxOutputTokensPerRequest);
    }

}
