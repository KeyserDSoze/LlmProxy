using LlmProxy.Api.OpenAi;
using Microsoft.AspNetCore.Http;

namespace LlmProxy.UnitTests.OpenAi;

public sealed class UpstreamHeaderForwardingTests
{
    [Fact]
    public void Caller_credentials_cookies_and_hop_by_hop_headers_are_not_forwarded()
    {
        var context = new DefaultHttpContext();
        var headers = context.Request.Headers;
        headers.Authorization = "Bearer llmproxy-client-key";
        headers.Cookie = "llmproxy.admin=session";
        headers["Proxy-Authorization"] = "Basic secret";
        headers.Connection = "keep-alive";
        headers["Keep-Alive"] = "timeout=5";
        headers.Upgrade = "websocket";
        headers["OpenAI-Beta"] = "responses=v1";
        headers["X-Request-Id"] = "abc";
        using var upstream = new HttpRequestMessage(HttpMethod.Post, "http://node/v1/chat/completions")
        {
            Content = new StringContent("{}")
        };

        OpenAiEndpoints.CopyRequestHeaders(context.Request, upstream);

        Assert.False(upstream.Headers.Contains("Authorization"));
        Assert.False(upstream.Headers.Contains("Cookie"));
        Assert.False(upstream.Headers.Contains("Proxy-Authorization"));
        Assert.False(upstream.Headers.Contains("Keep-Alive"));
        Assert.False(upstream.Headers.Contains("Upgrade"));
        Assert.Equal("responses=v1", upstream.Headers.GetValues("OpenAI-Beta").Single());
        Assert.Equal("abc", upstream.Headers.GetValues("X-Request-Id").Single());
    }
}
