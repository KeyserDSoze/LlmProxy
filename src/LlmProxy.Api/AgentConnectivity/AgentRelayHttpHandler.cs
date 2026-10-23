namespace LlmProxy.Api.AgentConnectivity;

/// <summary>
/// For safe read-only probes, tolerate a brief WebSocket session hand-off or
/// Redis owner-lease rollover. Never replay POST/PUT/DELETE: Start, Stop and
/// Uninstall might already have taken effect on the remote Agent.
/// </summary>
public sealed class AgentRelayHttpHandler(AgentRelayHub hub, IServiceProvider services) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri;
        if (uri is null || !AgentRelayHub.TryParseNode(uri, out var nodeId))
            return await base.SendAsync(request, token);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (hub.IsConnected(nodeId))
                    return await hub.SendAsync(nodeId, request, token);
                var distributed = services.GetService<RedisAgentRelayBridge>();
                return distributed is null
                    ? await hub.SendAsync(nodeId, request, token)
                    : await distributed.ForwardAsync(nodeId, request, token);
            }
            catch (Exception error) when (
                request.Method == HttpMethod.Get && attempt < 2 &&
                !token.IsCancellationRequested &&
                error is HttpRequestException or IOException)
            {
                await Task.Delay(attempt == 0 ? 300 : 900, token);
            }
        }
    }
}
