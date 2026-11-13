using LlmProxy.Api.OpenAi;
using LlmProxy.Application.Routing;

namespace LlmProxy.UnitTests.Api;

public sealed class GatewayRoutingErrorsTests
{
    [Theory]
    [InlineData(RoutingSelectionFailure.ModelNotFound, 404, "model_not_found", null)]
    [InlineData(RoutingSelectionFailure.NotRunning, 503, "model_not_running", 30)]
    [InlineData(RoutingSelectionFailure.CapacityExhausted, 429, "capacity_exhausted", 1)]
    [InlineData(RoutingSelectionFailure.Unavailable, 503, "no_healthy_deployment", null)]
    public void Each_routing_failure_has_its_own_public_status_and_code(
        RoutingSelectionFailure failure, int status, string code, int? retryAfter)
    {
        var error = GatewayRoutingError.For(failure, "agic-code");

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(code, error.Code);
        Assert.Equal(retryAfter, error.RetryAfterSeconds);
        Assert.Contains("'agic-code'", error.Message);
    }

    [Fact]
    public void Public_messages_never_expose_server_or_provider_identifiers()
    {
        foreach (var failure in Enum.GetValues<RoutingSelectionFailure>())
        {
            var message = GatewayRoutingError.For(failure, "agic-code").Message;
            Assert.DoesNotContain("http", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("node", message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
