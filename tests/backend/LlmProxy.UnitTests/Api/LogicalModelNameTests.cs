using LlmProxy.Domain.Models;

namespace LlmProxy.UnitTests.Api;

public sealed class LogicalModelNameTests
{
    [Fact]
    public void Names_up_to_the_column_width_are_accepted_and_longer_ones_are_cut_for_persistence()
    {
        var atLimit = new string('a', LogicalModelName.MaxLength);
        var tooLong = atLimit + "b";

        Assert.True(LogicalModelName.IsAcceptable(atLimit));
        Assert.False(LogicalModelName.IsAcceptable(tooLong));
        Assert.Equal(atLimit, LogicalModelName.Truncate(tooLong));
        Assert.Equal("agic-code", LogicalModelName.Truncate("agic-code"));
    }
}
