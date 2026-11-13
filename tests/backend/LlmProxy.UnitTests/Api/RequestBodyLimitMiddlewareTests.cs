using System.Text.Json;
using LlmProxy.Api.OpenAi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace LlmProxy.UnitTests.Api;

public sealed class RequestBodyLimitMiddlewareTests
{
    private static DefaultHttpContext Post(string path, long? contentLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.ContentLength = contentLength;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Code(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task A_declared_body_over_the_limit_is_refused_before_anything_reads_it()
    {
        var called = false;
        var middleware = new RequestBodyLimitMiddleware(_ => { called = true; return Task.CompletedTask; }, new RequestBodyLimit(1000));
        var context = Post("/v1/chat/completions", 1001);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("request_too_large", Code(context));
    }

    [Fact]
    public async Task A_body_within_the_limit_passes_and_the_server_limit_is_lowered()
    {
        var feature = new FakeMaxBodyFeature { MaxRequestBodySize = 30_000_000 };
        var called = false;
        var middleware = new RequestBodyLimitMiddleware(_ => { called = true; return Task.CompletedTask; }, new RequestBodyLimit(1000));
        var context = Post("/v1/responses", 1000);
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(1000, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task A_chunked_body_that_exceeds_the_limit_while_being_read_becomes_a_413()
    {
        var middleware = new RequestBodyLimitMiddleware(
            _ => throw new BadHttpRequestException("too large", StatusCodes.Status413PayloadTooLarge), new RequestBodyLimit(1000));
        var context = Post("/v1/systemone/classifier", null);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("request_too_large", Code(context));
    }

    [Theory]
    [InlineData("/v1/audio/transcriptions")]
    [InlineData("/v1/images/generations")]
    [InlineData("/api/admin/models")]
    public async Task Other_routes_are_not_limited_here(string path)
    {
        var called = false;
        var middleware = new RequestBodyLimitMiddleware(_ => { called = true; return Task.CompletedTask; }, new RequestBodyLimit(1000));

        await middleware.InvokeAsync(Post(path, 50_000_000));

        Assert.True(called);
    }

    [Theory]
    [InlineData(null, RequestBodyLimit.Default)]
    [InlineData("", RequestBodyLimit.Default)]
    [InlineData("abc", RequestBodyLimit.Default)]
    [InlineData("-5", RequestBodyLimit.Default)]
    [InlineData("100", RequestBodyLimit.Default)]
    [InlineData("999999999999", RequestBodyLimit.Default)]
    [InlineData("1048576", 1048576)]
    public void Configuration_is_bounded_and_falls_back_to_the_default(string? raw, long expected)
        => Assert.Equal(expected, RequestBodyLimit.Read(raw));

    private sealed class FakeMaxBodyFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }
}
