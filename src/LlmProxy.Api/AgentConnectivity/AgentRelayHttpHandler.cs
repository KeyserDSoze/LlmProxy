namespace LlmProxy.Api.AgentConnectivity;

/// <summary>Intercepts only the non-DNS .invalid virtual host. All normal HTTP clients are unaffected.</summary>
public sealed class AgentRelayHttpHandler(AgentRelayHub hub, IServiceProvider services) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri;
        if (uri is null || !AgentRelayHub.TryParseNode(uri, out var nodeId))
            return base.SendAsync(request, token);
        if (hub.IsConnected(nodeId)) return hub.SendAsync(nodeId, request, token);
        var distributed = services.GetService<RedisAgentRelayBridge>();
        return distributed is null ? hub.SendAsync(nodeId, request, token)
            : distributed.ForwardAsync(nodeId, request, token);
    }
}
