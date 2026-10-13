using LlmProxy.Api.SystemOne;

namespace LlmProxy.UnitTests.Api;

public sealed class SystemOneDefaultResolverTests
{
    [Fact]
    public void Empty_catalog_has_no_default() =>
        Assert.Null(SystemOneDefaultResolver.Resolve([], "old", "laya"));

    [Fact]
    public void Persisted_selection_wins_over_environment_and_order() =>
        Assert.Equal("my-laya", SystemOneDefaultResolver.Resolve(
            ["z", "my-laya", "laya-english-cpu"], "my-laya", "z"));

    [Fact]
    public void Stale_persisted_and_environment_values_fall_back_to_live_laya() =>
        Assert.Equal("laya-english-cpu", SystemOneDefaultResolver.Resolve(
            ["z", "laya-english-cpu"], "uninstalled", "disabled"));

    [Fact]
    public void Fallback_does_not_depend_on_database_enumeration_order() =>
        Assert.Equal("alpha", SystemOneDefaultResolver.Resolve(
            ["zeta", "alpha", "zeta"], null, null));

    [Fact]
    public void Trim_and_use_valid_environment_when_no_explicit_selection() =>
        Assert.Equal("zeta", SystemOneDefaultResolver.Resolve(
            ["alpha", "zeta"], null, "  zeta  "));
}
