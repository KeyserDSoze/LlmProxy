using System.Net;
using System.Text;
using LlmProxy.Api.Product;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.UnitTests.Api;

public sealed class ReleaseDiscoveryServiceTests
{
    [Fact]
    public async Task Published_releases_expose_standard_and_custom_update_plans()
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/releases?", StringComparison.Ordinal))
            {
                return Json("""
                    [
                      {
                        "tag_name":"v0.0.8",
                        "name":"LlmProxy 0.0.8",
                        "draft":false,
                        "prerelease":false,
                        "published_at":"2026-10-03T16:00:00Z",
                        "html_url":"https://example.test/v0.0.8",
                        "assets":[
                          {
                            "name":"llmproxy-update-plan.json",
                            "browser_download_url":"https://assets.example.test/v0.0.8.json"
                          }
                        ]
                      },
                      {
                        "tag_name":"v0.0.7",
                        "name":"LlmProxy 0.0.7",
                        "draft":false,
                        "prerelease":false,
                        "published_at":"2026-10-03T15:00:00Z",
                        "html_url":"https://example.test/v0.0.7",
                        "assets":[]
                      },
                      {
                        "tag_name":"v0.0.5",
                        "name":"LlmProxy 0.0.5",
                        "draft":false,
                        "prerelease":false,
                        "published_at":"2026-10-02T15:00:00Z",
                        "html_url":"https://example.test/v0.0.5",
                        "assets":[]
                      }
                    ]
                    """);
            }

            if (url == "https://assets.example.test/v0.0.8.json")
            {
                return Json("""
                    {
                      "schemaVersion":1,
                      "mode":"custom",
                      "title":"Host service migration",
                      "description":"Runs the release-bundled migration before deployment.",
                      "requiresHostRestart":true,
                      "operatorCommand":"sudo -E llmproxyctl update {version}"
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var factory = new SingleClientFactory(client);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Updates:Repository"] = "KeyserDSoze/LlmProxy"
            })
            .Build();
        var service = new ReleaseDiscoveryService(factory, cache, configuration);

        var releases = await service.GetAvailableAsync("0.0.6", CancellationToken.None);

        Assert.Equal(3, releases.Count);
        Assert.Equal("0.0.8", releases[0].Version);
        Assert.True(releases[0].IsNewer);
        Assert.Equal("custom", releases[0].UpdateMode);
        Assert.True(releases[0].RequiresHostRestart);
        Assert.Equal("sudo -E llmproxyctl update 0.0.8", releases[0].OperatorCommand);

        Assert.Equal("0.0.7", releases[1].Version);
        Assert.True(releases[1].IsNewer);
        Assert.Equal("standard", releases[1].UpdateMode);
        Assert.Equal("sudo -E llmproxyctl update 0.0.7", releases[1].OperatorCommand);

        Assert.Equal("0.0.5", releases[2].Version);
        Assert.False(releases[2].IsNewer);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
