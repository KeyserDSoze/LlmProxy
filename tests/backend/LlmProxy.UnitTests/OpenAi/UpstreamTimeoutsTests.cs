using LlmProxy.Api.OpenAi;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.UnitTests.OpenAi;

public sealed class UpstreamTimeoutsTests
{
    [Fact]
    public void Defaults_are_generous_enough_for_long_reasoning_before_the_first_byte()
    {
        var timeouts = UpstreamTimeouts.From(new ConfigurationBuilder().Build());

        Assert.Equal(TimeSpan.FromMinutes(15), timeouts.ResponseHeaders);
        Assert.Equal(TimeSpan.FromMinutes(5), timeouts.StreamIdle);
    }

    [Fact]
    public void Operators_can_tune_both_limits()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [UpstreamTimeouts.HeaderSecondsKey] = "120",
            [UpstreamTimeouts.IdleSecondsKey] = "45"
        }).Build();

        var timeouts = UpstreamTimeouts.From(configuration);

        Assert.Equal(TimeSpan.FromSeconds(120), timeouts.ResponseHeaders);
        Assert.Equal(TimeSpan.FromSeconds(45), timeouts.StreamIdle);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("999999")]
    [InlineData("")]
    public void Unusable_values_fall_back_instead_of_disabling_the_protection(string raw)
        => Assert.Equal(TimeSpan.FromMinutes(5), UpstreamTimeouts.Read(raw, TimeSpan.FromMinutes(5)));
}
