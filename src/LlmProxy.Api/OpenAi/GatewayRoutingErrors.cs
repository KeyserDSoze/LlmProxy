using LlmProxy.Application.Routing;

namespace LlmProxy.Api.OpenAi;

/// <summary>
/// Public, OpenAI-shaped answer for a routing failure. Messages name only the logical model the caller
/// asked for: servers, ports and provider identifiers stay internal.
/// </summary>
public readonly record struct GatewayRoutingError(int StatusCode, string Type, string Code, string Message, int? RetryAfterSeconds)
{
    public static GatewayRoutingError For(RoutingSelectionFailure failure, string publicModelName) => failure switch
    {
        RoutingSelectionFailure.ModelNotFound => new(
            StatusCodes.Status404NotFound, "invalid_request_error", "model_not_found",
            $"The model '{publicModelName}' does not exist or is not enabled. List the available models with GET /v1/models.", null),
        RoutingSelectionFailure.NotRunning => new(
            StatusCodes.Status503ServiceUnavailable, "gateway_unavailable", "model_not_running",
            $"The model '{publicModelName}' exists but is not running right now (stopped, starting or not yet activated). Retry later or contact the administrator.", 30),
        RoutingSelectionFailure.CapacityExhausted => new(
            StatusCodes.Status429TooManyRequests, "rate_limit_error", "capacity_exhausted",
            $"Inference capacity is temporarily exhausted for model '{publicModelName}'. Retry shortly.", 1),
        _ => new(
            StatusCodes.Status503ServiceUnavailable, "gateway_unavailable", "no_healthy_deployment",
            $"No healthy deployment is available for model '{publicModelName}'.", null)
    };
}
