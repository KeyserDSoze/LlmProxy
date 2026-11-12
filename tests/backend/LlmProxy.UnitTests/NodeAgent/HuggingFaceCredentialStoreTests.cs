using System.Net;
using System.Text.Json;
using LlmProxy.NodeAgent;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class HuggingFaceCredentialStoreTests : IDisposable
{
    private const string Token = "hf_exampleReadToken1234567890";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "llmproxy-hf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private NodeAgentOptions Options(string? environmentToken = null) =>
        new(null, "docker", "vllm", _root, _root, "localhost", 18000, true, true, 60, 20, "llama", "sglang",
            HuggingFaceToken: environmentToken);

    private HuggingFaceCredentialStore Store(Hub hub, string? environmentToken = null) =>
        new(Options(environmentToken), new Clients(new HttpClient(hub)),
            NullLogger<HuggingFaceCredentialStore>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too short")]
    [InlineData("hf_has space inside")]
    [InlineData("hf_line\nbreak123456")]
    public async Task Values_that_are_not_tokens_are_rejected_without_contacting_Hugging_Face(string? value)
    {
        var hub = new Hub(HttpStatusCode.OK, "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => Store(hub).SetAsync(value, TestContext.Current.CancellationToken));
        Assert.Empty(hub.Requests);
    }

    [Fact]
    public async Task Rejected_token_is_not_stored()
    {
        var store = Store(new Hub(HttpStatusCode.Unauthorized, "{}"));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(Token, TestContext.Current.CancellationToken));
        Assert.Contains("rejected", error.Message);
        Assert.Null(store.Token);
        Assert.False(store.Status().Configured);
        Assert.False(File.Exists(Path.Combine(_root, "huggingface-credential.json")));
    }

    [Fact]
    public async Task Verified_token_is_stored_owner_only_and_survives_a_restart()
    {
        var hub = new Hub(HttpStatusCode.OK, """{"name":"alessandro","type":"user"}""");
        var store = Store(hub);

        var status = await store.SetAsync("  " + Token + "  ", TestContext.Current.CancellationToken);

        Assert.Equal(new HuggingFaceAccessStatus(true, "admin", "alessandro", true, status.UpdatedAtUtc), status);
        Assert.Equal(Token, store.Token);
        Assert.Equal("Bearer " + Token, hub.Authorization.Single());
        Assert.EndsWith("/api/whoami-v2", hub.Requests.Single());
        var path = Path.Combine(_root, "huggingface-credential.json");
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var restarted = Store(hub);
        Assert.Equal(Token, restarted.Token);
        Assert.Equal("alessandro", restarted.Status().Account);
    }

    [Fact]
    public async Task Status_never_discloses_the_token()
    {
        var store = Store(new Hub(HttpStatusCode.OK, """{"name":"alessandro"}"""));
        var status = await store.SetAsync(Token, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Unreachable_Hugging_Face_still_saves_the_token_but_reports_it_unverified()
    {
        var store = Store(new Hub(HttpStatusCode.InternalServerError, "oops"));
        var status = await store.SetAsync(Token, TestContext.Current.CancellationToken);
        Assert.True(status.Configured);
        Assert.False(status.Verified);
        Assert.Null(status.Account);
        Assert.Equal(Token, store.Token);
    }

    [Fact]
    public async Task Environment_token_is_a_fallback_that_the_admin_token_overrides_and_clearing_restores()
    {
        var store = Store(new Hub(HttpStatusCode.OK, """{"name":"alessandro"}"""), "hf_environmentToken123456");
        Assert.Equal(new HuggingFaceAccessStatus(true, "environment", null, false, null), store.Status());
        Assert.Equal("hf_environmentToken123456", store.Token);

        await store.SetAsync(Token, TestContext.Current.CancellationToken);
        Assert.Equal("admin", store.Status().Source);
        Assert.Equal(Token, store.Token);

        var cleared = await store.ClearAsync(TestContext.Current.CancellationToken);
        Assert.Equal("environment", cleared.Source);
        Assert.Equal("hf_environmentToken123456", store.Token);
        Assert.False(File.Exists(Path.Combine(_root, "huggingface-credential.json")));
    }

    [Fact]
    public async Task Child_processes_receive_the_token_through_the_environment_only()
    {
        var store = Store(new Hub(HttpStatusCode.OK, """{"name":"alessandro"}"""));
        Assert.Null(store.ProcessEnvironment());
        await store.SetAsync(Token, TestContext.Current.CancellationToken);
        Assert.Equal(Token, store.ProcessEnvironment()!["HF_TOKEN"]);
    }

    [Fact]
    public async Task Unreadable_credential_file_is_ignored_instead_of_breaking_the_agent()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "huggingface-credential.json"), "{ not json",
            TestContext.Current.CancellationToken);
        var store = Store(new Hub(HttpStatusCode.OK, "{}"));
        Assert.Null(store.Token);
        Assert.Equal("none", store.Status().Source);
    }

    [Theory]
    [InlineData("huggingface_hub.errors.GatedRepoError: 403 Client Error. Cannot access gated repo", false, "(HTTP 401)")]
    [InlineData("huggingface_hub.errors.RepositoryNotFoundError: 401 Client Error.", false, "requires authentication")]
    [InlineData("GatedRepoError: 403 Client Error", true, "(HTTP 403)")]
    [InlineData("401 Client Error: Unauthorized for url", true, "rejected the configured access token")]
    public void Prefetch_container_failures_are_translated_into_the_guided_message(
        string output, bool hasToken, string expected)
    {
        var message = HuggingFaceCredentialStore.ExplainAccessFailure("org/repo", output, hasToken);
        Assert.NotNull(message);
        Assert.Contains("'org/repo'", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void Unrelated_failures_are_left_untouched()
    {
        Assert.Null(HuggingFaceCredentialStore.ExplainAccessFailure("org/repo", "No space left on device", false));
    }

    private sealed class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Hub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (request.Headers.Authorization is { } header) Authorization.Add(header.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
