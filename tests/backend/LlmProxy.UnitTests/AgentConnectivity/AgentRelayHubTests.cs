using LlmProxy.Api.AgentConnectivity;

namespace LlmProxy.UnitTests.AgentConnectivity;

public sealed class AgentRelayHubTests
{
    [Fact]
    public void Virtual_relay_endpoint_routes_only_exact_agent_guid()
    {
        var id = Guid.NewGuid();
        var root = AgentRelayHub.Root(id);
        Assert.True(AgentRelayHub.TryParseNode(new Uri(root + "/runtime/18000/v1/chat/completions"), out var parsed));
        Assert.Equal(id, parsed);
        Assert.True(AgentRelayHub.TryParseNode(new Uri(root + "/management/v1/system"), out parsed));
        Assert.Equal(id, parsed);
        Assert.False(AgentRelayHub.TryParseNode(new Uri("https://example.org/v1/models"), out _));
        Assert.False(AgentRelayHub.TryParseNode(new Uri("https://agent-hello.llmproxy.invalid/v1/models"), out _));
    }

    [Fact]
    public async Task Relay_fails_closed_without_local_agent_session()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            AgentRelayHub.Root(Guid.NewGuid()) + "/runtime/18000/v1/chat/completions");
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AgentRelayHub().SendAsync(Guid.NewGuid(), request, CancellationToken.None));
    }
}
