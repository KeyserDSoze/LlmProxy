using LlmProxy.Domain.Governance;

namespace LlmProxy.UnitTests.Domain;

public sealed class UsageGroupRateLimitPolicyTests
{
    [Fact]
    public void Group_quota_supports_request_and_output_token_limits()
    {
        var groupId = Guid.NewGuid();
        var policy = new UsageGroupRateLimitPolicy(
            groupId,
            " agic-code ",
            500,
            60,
            true,
            250000,
            4096);

        Assert.Equal(groupId, policy.UsageGroupId);
        Assert.Equal("agic-code", policy.LogicalModel);
        Assert.Equal(500, policy.RequestsPerWindow);
        Assert.Equal(250000, policy.OutputTokensPerWindow);
        Assert.Equal(4096, policy.MaxOutputTokensPerRequest);

        policy.ClearOutputTokenBudget();

        Assert.Null(policy.OutputTokensPerWindow);
        Assert.Null(policy.MaxOutputTokensPerRequest);
    }

    [Fact]
    public void Group_quota_requires_real_group_id()
    {
        Assert.Throws<ArgumentException>(() => new UsageGroupRateLimitPolicy(Guid.Empty, null, 10));
    }
}
