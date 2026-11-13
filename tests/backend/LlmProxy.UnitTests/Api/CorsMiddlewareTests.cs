using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Security;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace LlmProxy.UnitTests.Api;

public sealed class CorsMiddlewareTests
{
    private const string Site = "https://ghostwriting.ws";

    private static (CorsPolicyState Policy, InMemoryApiCredentialCache Cache) State(
        string[]? global = null, params ApiCredentialSnapshot[] credentials)
    {
        var policy = new CorsPolicyState();
        policy.Replace(new CorsSettings(global ?? [], 600));
        var cache = new InMemoryApiCredentialCache();
        cache.Replace(credentials);
        return (policy, cache);
    }

    private static ApiCredentialSnapshot Key(string hash, bool enabled = true, params string[] origins) =>
        new(Guid.NewGuid(), hash, enabled, null, null, AllowedOrigins: origins.Length == 0 ? null : origins);

    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));

        public async Task FireStartingAsync()
        {
            foreach (var (callback, state) in _starting) await callback(state);
        }
    }

    private static (DefaultHttpContext Context, CapturingResponseFeature Feature) Request(
        string method, string path, string? origin, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        var feature = new CapturingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(feature);
        context.Response.Body = new MemoryStream();
        context.Request.Method = method;
        context.Request.Path = path;
        if (origin is not null) context.Request.Headers["Origin"] = origin;
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        return (context, feature);
    }

    private static async Task<bool> RunCorsAsync(DefaultHttpContext context, CorsPolicyState policy, IApiCredentialCache cache,
        Action<HttpContext>? downstream = null)
    {
        var called = false;
        var middleware = new InferenceCorsMiddleware(ctx =>
        {
            called = true;
            downstream?.Invoke(ctx);
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context, policy, cache);
        return called;
    }

    [Fact]
    public async Task Preflight_from_a_site_wide_origin_is_answered_without_calling_the_pipeline()
    {
        var (policy, cache) = State([Site]);
        var (context, _) = Request("OPTIONS", "/v1/models", Site,
            ("Access-Control-Request-Method", "GET"), ("Access-Control-Request-Headers", "authorization, content-type"));

        var called = await RunCorsAsync(context, policy, cache);

        Assert.False(called);
        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(Site, context.Response.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal("authorization, content-type", context.Response.Headers["Access-Control-Allow-Headers"]);
        Assert.Equal("GET, POST, OPTIONS", context.Response.Headers["Access-Control-Allow-Methods"]);
        Assert.Equal("600", context.Response.Headers["Access-Control-Max-Age"]);
        Assert.Contains("Origin", context.Response.Headers.Vary.ToString());
    }

    [Fact]
    public async Task Preflight_is_allowed_when_any_enabled_key_lists_the_origin()
    {
        var (policy, cache) = State([], Key("a", true, Site));
        var (context, _) = Request("OPTIONS", "/v1/chat/completions", Site, ("Access-Control-Request-Method", "POST"));

        await RunCorsAsync(context, policy, cache);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(Site, context.Response.Headers["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task Preflight_from_an_unknown_origin_is_rejected_without_cors_headers()
    {
        var (policy, cache) = State(["https://other.example"], Key("a", false, Site));
        var (context, _) = Request("OPTIONS", "/v1/models", Site, ("Access-Control-Request-Method", "GET"));

        var called = await RunCorsAsync(context, policy, cache);

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        context.Response.Body.Position = 0;
        Assert.Contains("origin_not_allowed", await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not an origin")]
    [InlineData("https://ghostwriting.ws/path")]
    public async Task Malformed_origins_never_pass_a_preflight(string origin)
    {
        var (policy, cache) = State([OriginPolicy.Wildcard]);
        var (context, _) = Request("OPTIONS", "/v1/models", origin, ("Access-Control-Request-Method", "GET"));

        await RunCorsAsync(context, policy, cache);

        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task Wildcard_allows_any_well_formed_origin_and_echoes_it()
    {
        var (policy, cache) = State([OriginPolicy.Wildcard]);
        var (context, _) = Request("OPTIONS", "/v1/models", "http://localhost:5173", ("Access-Control-Request-Method", "GET"));

        await RunCorsAsync(context, policy, cache);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal("http://localhost:5173", context.Response.Headers["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task Requested_headers_with_unexpected_characters_fall_back_to_the_default_list()
    {
        var (policy, cache) = State([Site]);
        var (context, _) = Request("OPTIONS", "/v1/models", Site,
            ("Access-Control-Request-Method", "GET"), ("Access-Control-Request-Headers", "authorization; evil=1"));

        await RunCorsAsync(context, policy, cache);

        Assert.Equal("authorization, content-type", context.Response.Headers["Access-Control-Allow-Headers"]);
    }

    [Fact]
    public async Task Requests_without_origin_or_outside_v1_are_left_alone()
    {
        var (policy, cache) = State([Site]);

        var (noOrigin, noOriginFeature) = Request("GET", "/v1/models", null);
        Assert.True(await RunCorsAsync(noOrigin, policy, cache));
        await noOriginFeature.FireStartingAsync();
        Assert.False(noOrigin.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));

        var (admin, adminFeature) = Request("GET", "/api/admin/models", Site);
        Assert.True(await RunCorsAsync(admin, policy, cache));
        await adminFeature.FireStartingAsync();
        Assert.False(admin.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Actual_response_gets_cors_headers_for_site_wide_origins()
    {
        var (policy, cache) = State([Site]);
        var (context, feature) = Request("POST", "/v1/chat/completions", Site);

        await RunCorsAsync(context, policy, cache);
        await feature.FireStartingAsync();

        Assert.Equal(Site, context.Response.Headers["Access-Control-Allow-Origin"]);
        var exposed = context.Response.Headers["Access-Control-Expose-Headers"].ToString();
        Assert.Contains("Retry-After", exposed);
        Assert.Contains("X-LlmProxy-Request-Id", exposed);
        Assert.Contains("X-LlmProxy-Trace-Id", exposed);
    }

    [Fact]
    public async Task Actual_response_uses_the_authenticated_keys_own_origins()
    {
        var (policy, cache) = State([]);
        var (allowed, allowedFeature) = Request("GET", "/v1/models", Site);
        await RunCorsAsync(allowed, policy, cache, ctx =>
        {
            ctx.Items[InferenceApiKeyMiddleware.ApiCredentialIdItem] = Guid.NewGuid();
            ctx.Items[InferenceCorsMiddleware.CredentialOriginsItem] = (IReadOnlyList<string>)[Site];
        });
        await allowedFeature.FireStartingAsync();
        Assert.Equal(Site, allowed.Response.Headers["Access-Control-Allow-Origin"]);

        var (denied, deniedFeature) = Request("GET", "/v1/models", "https://evil.example");
        await RunCorsAsync(denied, policy, cache, ctx =>
        {
            ctx.Items[InferenceApiKeyMiddleware.ApiCredentialIdItem] = Guid.NewGuid();
            ctx.Items[InferenceCorsMiddleware.CredentialOriginsItem] = (IReadOnlyList<string>)[Site];
        });
        await deniedFeature.FireStartingAsync();
        Assert.False(denied.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Invalid_key_response_is_readable_by_a_browser_when_some_key_lists_the_origin()
    {
        var (policy, cache) = State([], Key("a", true, Site));
        var (context, feature) = Request("GET", "/v1/models", Site);

        await RunCorsAsync(context, policy, cache);
        await feature.FireStartingAsync();

        Assert.Equal(Site, context.Response.Headers["Access-Control-Allow-Origin"]);
    }

    // --- Key enforcement ---------------------------------------------------------------------

    private sealed class Usage : ICredentialUsageSink
    {
        public List<Guid> Recorded { get; } = [];
        public void RecordUsage(Guid credentialId, DateTimeOffset usedAtUtc) => Recorded.Add(credentialId);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<(bool Called, int Status, Usage Usage)> RunKeyAsync(
        string? origin, CorsPolicyState policy, string[]? keyOrigins)
    {
        var hasher = new ApiKeyHasher(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Authentication:ApiKeyPepper"] = "test-pepper" }).Build(),
            new TestEnvironment());
        const string secret = "lp_org_secret";
        var credential = new ApiCredentialSnapshot(Guid.NewGuid(), hasher.Hash(secret), true, null, null, AllowedOrigins: keyOrigins);
        var cache = new InMemoryApiCredentialCache();
        cache.Replace([credential]);
        var usage = new Usage();
        var (context, _) = Request("GET", "/v1/models", origin, ("Authorization", "Bearer " + secret));
        var called = false;
        var middleware = new InferenceApiKeyMiddleware(_ => { called = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context, hasher, cache, usage, policy);
        return (called, context.Response.StatusCode, usage);
    }

    [Fact]
    public async Task Key_with_origins_is_rejected_from_other_browser_origins()
    {
        var (policy, _) = State([]);
        var (called, status, usage) = await RunKeyAsync("https://evil.example", policy, [Site]);
        Assert.False(called);
        Assert.Equal(403, status);
        Assert.Empty(usage.Recorded);
    }

    [Theory]
    [InlineData("https://ghostwriting.ws")]
    [InlineData("https://GHOSTWRITING.ws")]
    public async Task Key_with_origins_accepts_its_own_origin(string origin)
    {
        var (policy, _) = State([]);
        var (called, _, usage) = await RunKeyAsync(origin, policy, [Site]);
        Assert.True(called);
        Assert.Single(usage.Recorded);
    }

    [Fact]
    public async Task Site_wide_origins_also_work_for_restricted_keys()
    {
        var (policy, _) = State(["https://admin-console.example"]);
        var (called, _, _) = await RunKeyAsync("https://admin-console.example", policy, [Site]);
        Assert.True(called);
    }

    [Fact]
    public async Task Restriction_does_not_apply_to_server_side_callers_without_an_origin()
    {
        var (policy, _) = State([]);
        var (called, _, _) = await RunKeyAsync(null, policy, [Site]);
        Assert.True(called);
    }

    [Fact]
    public async Task Key_without_origins_is_not_restricted()
    {
        var (policy, _) = State([]);
        var (called, _, _) = await RunKeyAsync("https://anything.example", policy, null);
        Assert.True(called);
    }

    [Fact]
    public async Task Unparseable_origin_is_rejected_for_a_restricted_key()
    {
        var (policy, _) = State([]);
        var (called, status, _) = await RunKeyAsync("null", policy, [Site]);
        Assert.False(called);
        Assert.Equal(403, status);
    }
}
