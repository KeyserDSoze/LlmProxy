namespace LlmProxy.UnitTests.AgentConnectivity;

public sealed class RelayPathGuardTests
{
    public static TheoryData<string?, bool> Cases => new()
    {
        { "/runtime/18000/v1/chat/completions", true },
        { "/management/v1/models/install-jobs/abc", true },
        { "/runtime/18000/health?probe=1", true },
        { "/runtime/18000/v1/../metrics", false },
        { "/runtime/18000/v1/%2e%2e/%2e%2e/metrics", false },
        { "/runtime/18000/v1/%2E%2E/metrics", false },
        { "/runtime/18000/v1/..%2fmetrics", false },
        { "/runtime/18000/v1%2fchat", false },
        { "/runtime/18000/v1/%5cadmin", false },
        { "/runtime/18000/v1\\chat", false },
        { "/runtime/18000/v1/chat\n", false },
        { "/runtime/18000/v1/%00", false },
        { "runtime/18000", false },
        { "", false },
        { null, false }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Node_agent_accepts_only_paths_that_stay_what_the_allow_list_approved(string? path, bool safe)
        => Assert.Equal(safe, LlmProxy.NodeAgent.RelayPathGuard.IsSafe(path));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gateway_bridge_applies_the_same_rule(string? path, bool safe)
        => Assert.Equal(safe, LlmProxy.Api.AgentConnectivity.RelayPathGuard.IsSafe(path));

    [Fact]
    public void The_bypass_the_guard_closes_is_real_uri_normalisation_resolves_encoded_dot_segments()
    {
        var resolved = new Uri("http://127.0.0.1:18000/v1/%2e%2e/%2e%2e/metrics");

        Assert.Equal("/metrics", resolved.AbsolutePath);
    }
}
