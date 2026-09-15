using System.Text.Json.Nodes;
using LlmProxy.Api.OpenAi;
using Microsoft.AspNetCore.Http;

namespace LlmProxy.UnitTests.OpenAi;

public sealed class OutputTokenRequestPolicyTests
{
    [Fact]
    public void Chat_injects_limit_when_client_did_not_supply_one()
    {
        var payload = JsonNode.Parse("{\"model\":\"agic-code\",\"messages\":[]}")!.AsObject();

        var ok = OutputTokenRequestPolicy.TryApply(
            payload,
            new PathString("/v1/chat/completions"),
            256,
            out var reservation,
            out _,
            out _);

        Assert.True(ok);
        Assert.Equal(256, reservation);
        Assert.Equal(256, payload["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Chat_caps_both_supported_limit_fields_and_reserves_the_larger_effective_value()
    {
        var payload = JsonNode.Parse("{\"model\":\"agic-code\",\"max_tokens\":500,\"max_completion_tokens\":128}")!.AsObject();

        var ok = OutputTokenRequestPolicy.TryApply(
            payload,
            new PathString("/v1/chat/completions"),
            256,
            out var reservation,
            out _,
            out _);

        Assert.True(ok);
        Assert.Equal(256, reservation);
        Assert.Equal(256, payload["max_tokens"]!.GetValue<int>());
        Assert.Equal(128, payload["max_completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Responses_caps_max_output_tokens()
    {
        var payload = JsonNode.Parse("{\"model\":\"agic-code\",\"max_output_tokens\":1000}")!.AsObject();

        var ok = OutputTokenRequestPolicy.TryApply(
            payload,
            new PathString("/v1/responses"),
            300,
            out var reservation,
            out _,
            out _);

        Assert.True(ok);
        Assert.Equal(300, reservation);
        Assert.Equal(300, payload["max_output_tokens"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{\"model\":\"agic-code\",\"max_tokens\":0}", "/v1/chat/completions")]
    [InlineData("{\"model\":\"agic-code\",\"max_output_tokens\":\"many\"}", "/v1/responses")]
    public void Invalid_output_limit_is_rejected(string raw, string path)
    {
        var payload = JsonNode.Parse(raw)!.AsObject();

        var ok = OutputTokenRequestPolicy.TryApply(
            payload,
            new PathString(path),
            256,
            out _,
            out var errorCode,
            out _);

        Assert.False(ok);
        Assert.Equal("invalid_output_token_limit", errorCode);
    }
}
