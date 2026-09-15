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

    [Fact]
    public void Output_token_budget_can_be_set_and_cleared_without_changing_request_rate()
    {
        var policy = new RateLimitPolicy(Guid.NewGuid(), "agic-code", 120, 60);

        policy.SetOutputTokenBudget(100_000, 4_096);

        Assert.Equal(100_000, policy.OutputTokensPerWindow);
        Assert.Equal(4_096, policy.MaxOutputTokensPerRequest);
        Assert.Equal(120, policy.RequestsPerWindow);
        Assert.Equal(60, policy.WindowSeconds);

        policy.ClearOutputTokenBudget();

        Assert.Null(policy.OutputTokensPerWindow);
        Assert.Null(policy.MaxOutputTokensPerRequest);
        Assert.Equal(120, policy.RequestsPerWindow);
    }

    [Fact]
    public void Request_rate_update_preserves_existing_output_token_budget()
    {
        var policy = new RateLimitPolicy(Guid.NewGuid(), null, 120, 60, outputTokensPerWindow: 100_000, maxOutputTokensPerRequest: 4_096);

        policy.Update("agic-code", 240, 120, enabled: true);

        Assert.Equal(100_000, policy.OutputTokensPerWindow);
        Assert.Equal(4_096, policy.MaxOutputTokensPerRequest);
    }

    [Theory]
    [InlineData(100, 101)]
    [InlineData(0, 1)]
    [InlineData(100, 0)]
    public void Output_token_budget_rejects_invalid_limits(int windowBudget, int maxPerRequest)
    {
        var policy = new RateLimitPolicy(Guid.NewGuid(), null, 120, 60);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            policy.SetOutputTokenBudget(windowBudget, maxPerRequest));
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
