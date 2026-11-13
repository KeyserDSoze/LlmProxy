using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.UnitTests.Domain;

public sealed class OriginPolicyTests
{
    [Theory]
    [InlineData("https://ghostwriting.ws", "https://ghostwriting.ws")]
    [InlineData("  HTTPS://GhostWriting.WS/  ", "https://ghostwriting.ws")]
    [InlineData("https://app.example.com:443", "https://app.example.com")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("https://[::1]:8443", "https://[::1]:8443")]
    [InlineData("*", "*")]
    public void Origins_are_normalized_to_the_form_browsers_send(string input, string expected)
    {
        Assert.True(OriginPolicy.TryNormalize(input, out var origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("ghostwriting.ws")]
    [InlineData("ftp://ghostwriting.ws")]
    [InlineData("https://ghostwriting.ws/app")]
    [InlineData("https://ghostwriting.ws?x=1")]
    [InlineData("https://ghostwriting.ws#top")]
    [InlineData("https://user:pass@ghostwriting.ws")]
    [InlineData("https://*.example.com")]
    public void Anything_that_is_not_scheme_host_port_is_rejected(string? input)
    {
        Assert.False(OriginPolicy.TryNormalize(input, out _));
    }

    [Fact]
    public void Lists_are_deduplicated_and_blank_lines_ignored()
    {
        var list = OriginPolicy.NormalizeList(["https://a.example", "", "HTTPS://A.EXAMPLE/", " https://b.example "]);
        Assert.Equal(new[] { "https://a.example", "https://b.example" }, list);
        Assert.Empty(OriginPolicy.NormalizeList(null));
    }

    [Fact]
    public void An_invalid_entry_is_named_in_the_error()
    {
        var error = Assert.Throws<ArgumentException>(() => OriginPolicy.NormalizeList(["https://ok.example", "https://bad.example/path"]));
        Assert.Contains("https://bad.example/path", error.Message);
    }

    [Fact]
    public void Too_many_origins_are_refused()
    {
        var many = Enumerable.Range(0, OriginPolicy.MaxOrigins + 1).Select(index => $"https://site{index}.example");
        Assert.Throws<ArgumentException>(() => OriginPolicy.NormalizeList(many));
    }

    [Fact]
    public void Stored_form_round_trips()
    {
        var stored = OriginPolicy.ToStored(["https://a.example", "http://b.example:8080"]);
        Assert.Equal(new[] { "https://a.example", "http://b.example:8080" }, OriginPolicy.ParseStored(stored));
        Assert.Null(OriginPolicy.ToStored([]));
        Assert.Empty(OriginPolicy.ParseStored(null));
    }

    [Fact]
    public void Matching_is_case_insensitive_and_honours_the_wildcard()
    {
        Assert.True(OriginPolicy.Matches(["https://a.example"], "https://A.example"));
        Assert.True(OriginPolicy.Matches(["*"], "https://anything.example"));
        Assert.False(OriginPolicy.Matches(["https://a.example"], "https://a.example:8443"));
        Assert.False(OriginPolicy.Matches(null, "https://a.example"));
    }

    [Fact]
    public void Credential_stores_normalized_origins_and_exposes_them_in_the_runtime_snapshot()
    {
        var credential = new ApiCredential("web app", "lp_org_abcd", "hash");
        Assert.Empty(credential.AllowedOrigins);

        credential.SetAllowedOrigins(["HTTPS://GhostWriting.ws", "https://ghostwriting.ws/"]);

        Assert.Equal(new[] { "https://ghostwriting.ws" }, credential.AllowedOrigins);
        Assert.Equal(new[] { "https://ghostwriting.ws" }, ApiCredentialSnapshot.From(credential).AllowedOrigins);

        credential.SetAllowedOrigins([]);
        Assert.Empty(credential.AllowedOrigins);
        Assert.Null(ApiCredentialSnapshot.From(credential).AllowedOrigins);
    }

    [Fact]
    public void Invalid_origin_leaves_the_credential_unchanged()
    {
        var credential = new ApiCredential("web app", "lp_org_abcd", "hash");
        credential.SetAllowedOrigins(["https://a.example"]);
        Assert.Throws<ArgumentException>(() => credential.SetAllowedOrigins(["https://b.example/path"]));
        Assert.Equal(new[] { "https://a.example" }, credential.AllowedOrigins);
    }

    [Fact]
    public void Credential_cache_answers_preflights_from_enabled_keys_only_and_follows_changes()
    {
        var cache = new InMemoryApiCredentialCache();
        var id = Guid.NewGuid();
        cache.Upsert(new ApiCredentialSnapshot(id, "h1", true, null, null, AllowedOrigins: ["https://a.example"]));
        cache.Upsert(new ApiCredentialSnapshot(Guid.NewGuid(), "h2", false, null, null, AllowedOrigins: ["https://revoked.example"]));

        Assert.True(cache.AnyAllowsOrigin("https://a.example"));
        Assert.True(cache.AnyAllowsOrigin("https://A.example"));
        Assert.False(cache.AnyAllowsOrigin("https://revoked.example"));

        cache.Upsert(new ApiCredentialSnapshot(id, "h1", true, null, null, AllowedOrigins: ["https://c.example"]));
        Assert.False(cache.AnyAllowsOrigin("https://a.example"));
        Assert.True(cache.AnyAllowsOrigin("https://c.example"));

        cache.Remove(id);
        Assert.False(cache.AnyAllowsOrigin("https://c.example"));
    }
}
