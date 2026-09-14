using LlmProxy.Domain.Governance;

namespace LlmProxy.UnitTests.Domain;

public sealed class UsageGovernanceDomainTests
{
    [Fact]
    public void Usage_group_normalizes_name_and_description()
    {
        var group = new UsageGroup("  Platform Team  ", "  Shared Copilot usage  ");

        Assert.Equal("Platform Team", group.Name);
        Assert.Equal("Shared Copilot usage", group.Description);
    }

    [Fact]
    public void Rate_limit_policy_normalizes_optional_model()
    {
        var policy = new RateLimitPolicy(Guid.NewGuid(), "  agic-code  ", 120, 60);

        Assert.Equal("agic-code", policy.LogicalModel);
        Assert.Equal(120, policy.RequestsPerWindow);
        Assert.Equal(60, policy.WindowSeconds);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 0)]
    public void Rate_limit_policy_rejects_invalid_limits(int requests, int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RateLimitPolicy(Guid.NewGuid(), null, requests, seconds));
    }
}
