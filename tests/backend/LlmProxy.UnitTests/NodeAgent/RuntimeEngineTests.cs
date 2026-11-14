using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class RuntimeEngineTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Inspect_output_is_reduced_to_what_the_inventory_shows()
    {
        var json = "{\"Id\":\"sha256:abc\",\"Created\":\"2026-11-01T10:20:30.123456789Z\",\"Size\":123456789,"
            + "\"RepoDigests\":[\"ghcr.io/ggml-org/llama.cpp@" + Digest + "\"],"
            + "\"Config\":{\"Labels\":{\"org.opencontainers.image.version\":\"b4567\",\"org.opencontainers.image.revision\":\"0123456789abcdef0123\"}}}";
        var image = RuntimeEngineParsing.ParseInspect(json);

        Assert.NotNull(image);
        Assert.Equal("sha256:abc", image.Id);
        Assert.Equal(123456789, image.SizeBytes);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 10, 20, 30, TimeSpan.Zero).Date, image.CreatedAtUtc!.Value.UtcDateTime.Date);
        Assert.Equal("b4567", RuntimeEngineParsing.VersionOf("ghcr.io/ggml-org/llama.cpp:server-cuda", image));
        Assert.Equal("0123456789ab", RuntimeEngineParsing.RevisionOf(image));
        Assert.Single(image.RepoDigests);
    }

    [Fact]
    public void Without_labels_the_version_is_the_tag_and_a_missing_tag_stays_empty()
    {
        var image = RuntimeEngineParsing.ParseInspect("""{"Id":"sha256:abc","Config":{"Labels":null}}""")!;

        Assert.Equal("0.3", RuntimeEngineParsing.VersionOf("llmproxy-airllm:0.3", image));
        Assert.Equal("server-cuda", RuntimeEngineParsing.VersionOf("ghcr.io/ggml-org/llama.cpp:server-cuda", image));
        Assert.Null(RuntimeEngineParsing.VersionOf("localhost:5000/engine", image));
        Assert.Null(RuntimeEngineParsing.RevisionOf(image));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"Id":""}""")]
    public void Unreadable_inspect_output_means_not_installed(string output) =>
        Assert.Null(RuntimeEngineParsing.ParseInspect(output));

    [Theory]
    [InlineData("vllm/vllm-openai:latest", "registry-1.docker.io", "vllm/vllm-openai", "latest")]
    [InlineData("alpine:3.20", "registry-1.docker.io", "library/alpine", "3.20")]
    [InlineData("alpine", "registry-1.docker.io", "library/alpine", "latest")]
    [InlineData("ghcr.io/ggml-org/llama.cpp:server-cuda", "ghcr.io", "ggml-org/llama.cpp", "server-cuda")]
    [InlineData("localhost:5000/team/engine:1", "localhost:5000", "team/engine", "1")]
    public void Image_references_resolve_to_registry_repository_and_tag(string image, string registry, string repository, string tag) =>
        Assert.Equal(new RegistryDigestClient.ImageReference(registry, repository, tag), RegistryDigestClient.Parse(image));

    [Fact]
    public void Pinned_or_empty_references_are_not_compared()
    {
        Assert.Null(RegistryDigestClient.Parse("alpine@" + Digest));
        Assert.Null(RegistryDigestClient.Parse(""));
    }

    [Fact]
    public void A_bearer_challenge_yields_realm_service_and_scope()
    {
        var parts = RegistryDigestClient.ParseChallenge("Bearer realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:ggml-org/llama.cpp:pull\"");

        Assert.Equal("https://ghcr.io/token", parts["realm"]);
        Assert.Equal("ghcr.io", parts["service"]);
        Assert.Equal("repository:ggml-org/llama.cpp:pull", parts["scope"]);
        Assert.Empty(RegistryDigestClient.ParseChallenge("Basic realm=\"x\""));
    }

    private sealed class FakeRegistry(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.Method + " " + request.RequestUri + (request.Headers.Authorization is null ? "" : " [auth]"));
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task The_digest_is_read_after_the_anonymous_token_handshake()
    {
        var handler = new FakeRegistry(request =>
        {
            if (request.RequestUri!.Host == "auth.example")
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t0k\"}") };
            if (request.Headers.Authorization is null)
            {
                var unauthorized = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
                unauthorized.Headers.WwwAuthenticate.ParseAdd("Bearer realm=\"https://auth.example/token\",service=\"registry\",scope=\"repository:library/alpine:pull\"");
                return unauthorized;
            }
            var ok = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            ok.Headers.Add("Docker-Content-Digest", Digest);
            return ok;
        });

        var digest = await RegistryDigestClient.GetDigestAsync(new HttpClient(handler), "alpine:3.20", CancellationToken.None);

        Assert.Equal(Digest, digest);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(handler.Requests, request => request.StartsWith("HEAD https://registry-1.docker.io/v2/library/alpine/manifests/3.20 [auth]"));
    }

    [Fact]
    public async Task A_registry_that_refuses_gives_no_digest_instead_of_a_guess()
    {
        var handler = new FakeRegistry(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        Assert.Null(await RegistryDigestClient.GetDigestAsync(new HttpClient(handler), "private.example/team/engine:1", CancellationToken.None));
    }

    [Fact]
    public void An_image_is_behind_only_when_the_registry_digest_is_not_one_it_was_pulled_with()
    {
        Assert.False(RuntimeEngineParsing.IsBehindRegistry([$"vllm/vllm-openai@{Digest}"], Digest));
        Assert.True(RuntimeEngineParsing.IsBehindRegistry(["vllm/vllm-openai@sha256:" + new string('a', 64)], Digest));
        // Built or loaded locally: nothing to compare against.
        Assert.False(RuntimeEngineParsing.IsBehindRegistry([], Digest));
    }

    [Fact]
    public void Every_engine_a_model_can_use_has_one_image_and_a_unique_id()
    {
        var options = NodeAgentOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var ids = RuntimeEngineCatalog.All.Select(engine => engine.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
        // The inventory and the model runtime must resolve the same image, or "installed" would lie.
        Assert.All(RuntimeEngineCatalog.All, engine => Assert.Equal(
            ManagedRuntimeProfiles.ImageForRuntime(engine.Id, options), RuntimeEngineCatalog.Image(engine, options)));
        Assert.All(RuntimeEngineCatalog.All.Where(engine => engine.Built), engine => Assert.False(string.IsNullOrEmpty(engine.BuildFolder)));
        Assert.Equal(options.LlamaCppDockerImage, RuntimeEngineCatalog.Image(RuntimeEngineCatalog.Find("llama.cpp")!, options));
        Assert.Null(RuntimeEngineCatalog.Find("unknown"));
    }

    [Theory]
    [InlineData("airllm", true, "Dockerfile.arm64")]
    [InlineData("diffusers", true, "Dockerfile.arm64")]
    [InlineData("faster-whisper", true, "Dockerfile")]
    [InlineData("airllm", false, "Dockerfile")]
    public void Dockerfile_follows_the_host_architecture_like_the_model_runtime(string id, bool arm64, string expected) =>
        Assert.Equal(expected, RuntimeEngineCatalog.DockerfileName(RuntimeEngineCatalog.Find(id)!, arm64));
}
