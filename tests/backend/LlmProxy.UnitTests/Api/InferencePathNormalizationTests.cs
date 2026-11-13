using LlmProxy.Api.Observability;
using LlmProxy.Api.OpenAi;
using LlmProxy.Api.Security;
using Microsoft.AspNetCore.Http;

namespace LlmProxy.UnitTests.Api;

public sealed class InferencePathNormalizationTests
{
    [Theory]
    [InlineData("/v1/chat/completions/", "/v1/chat/completions")]
    [InlineData("/v1/chat/completions//", "/v1/chat/completions")]
    [InlineData("/V1/Responses/", "/V1/Responses")]
    [InlineData("/v1/systemone/classifier/", "/v1/systemone/classifier")]
    [InlineData("/v1/chat/completions", "/v1/chat/completions")]
    [InlineData("/api/admin/models/", "/api/admin/models/")]
    [InlineData("/", "/")]
    public void Trailing_slashes_are_removed_only_below_v1(string raw, string expected)
        => Assert.Equal(expected, InferencePathNormalizationMiddleware.Canonicalize(new PathString(raw)).Value);

    [Fact]
    public async Task Middleware_rewrites_the_request_path_before_the_next_layer_runs()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions/";
        string? seen = null;
        var middleware = new InferencePathNormalizationMiddleware(ctx => { seen = ctx.Request.Path.Value; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.Equal("/v1/chat/completions", seen);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "chat_completions")]
    [InlineData("/V1/Chat/Completions", "chat_completions")]
    [InlineData("/v1/responses", "responses")]
    [InlineData("/v1/systemone", "systemone")]
    [InlineData("/v1/systemone/my-classifier", "systemone")]
    [InlineData("/v1/images/generations", "image_generation")]
    [InlineData("/v1/models", "unknown")]
    [InlineData("/api/admin/models", "unknown")]
    public void Content_log_recognises_every_inference_path_including_system_one_with_a_model_segment(string path, string surface)
        => Assert.Equal(surface, InferenceContentLoggingMiddleware.SurfaceFor(new PathString(path)));

    [Fact]
    public async Task Token_budget_applies_to_a_trailing_slash_request_once_the_path_is_canonical()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions/";
        Assert.False(OutputTokenBudgetMiddleware.IsInferencePost(context.Request));

        await new InferencePathNormalizationMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        Assert.True(OutputTokenBudgetMiddleware.IsInferencePost(context.Request));
    }
}
