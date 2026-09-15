using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;

namespace LlmProxy.Api.OpenAi;

public sealed class OutputTokenBudgetMiddleware(RequestDelegate next)
{
    internal const string CapturedRequestMetricItem = "LlmProxy.OutputTokenBudget.RequestMetric";
    internal const string ActiveReservationItem = "LlmProxy.OutputTokenBudget.Reservation";

    public async Task InvokeAsync(
        HttpContext context,
        RequestRateLimiter rateLimiter,
        OutputTokenBudgetLimiter budgetLimiter,
        IRequestMetricsSink metricsSink)
    {
        if (!IsInferencePost(context.Request) ||
            !context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var credentialValue) ||
            credentialValue is not Guid credentialId)
        {
            await next(context);
            return;
        }

        var originalBody = context.Request.Body;
        MemoryStream? replacementBody = null;
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        string? logicalModel = null;
        IOutputTokenBudgetReservation? reservation = null;

        try
        {
            string rawBody;
            using (var reader = new StreamReader(originalBody, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            {
                rawBody = await reader.ReadToEndAsync(context.RequestAborted);
            }

            if (!TryParseObjectAndModel(rawBody, out var requestObject, out logicalModel))
            {
                replacementBody = ReplaceBody(context, rawBody);
                await next(context);
                return;
            }

            var policy = rateLimiter.ResolvePolicy(credentialId, logicalModel);
            if (policy?.HasOutputTokenBudget != true || policy.MaxOutputTokensPerRequest is not int maxPerRequest)
            {
                replacementBody = ReplaceBody(context, rawBody);
                await next(context);
                return;
            }

            if (!OutputTokenRequestPolicy.TryApply(
                    requestObject,
                    context.Request.Path,
                    maxPerRequest,
                    out var reservationTokens,
                    out var errorCode,
                    out var errorMessage))
            {
                await WriteRejectedMetricAsync(
                    context,
                    metricsSink,
                    startedAtUtc,
                    stopwatch,
                    logicalModel,
                    StatusCodes.Status400BadRequest,
                    errorCode);
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid_request_error",
                    errorCode,
                    errorMessage);
                return;
            }

            var budgetDecision = await budgetLimiter.TryReserveAsync(
                policy,
                reservationTokens,
                DateTimeOffset.UtcNow,
                context.RequestAborted);

            if (!budgetDecision.Acquired || budgetDecision.Reservation is null)
            {
                if (budgetDecision.Failure == OutputTokenBudgetAdmissionFailure.BudgetExceeded)
                {
                    context.Response.Headers.RetryAfter = budgetDecision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await WriteRejectedMetricAsync(
                        context,
                        metricsSink,
                        startedAtUtc,
                        stopwatch,
                        logicalModel,
                        StatusCodes.Status429TooManyRequests,
                        "token_budget_exceeded");
                    await WriteErrorAsync(
                        context,
                        StatusCodes.Status429TooManyRequests,
                        "rate_limit_error",
                        "token_budget_exceeded",
                        $"Output-token budget exceeded for model '{logicalModel}'. Retry after {budgetDecision.RetryAfterSeconds} seconds.");
                    return;
                }

                context.Response.Headers.RetryAfter = "1";
                await WriteRejectedMetricAsync(
                    context,
                    metricsSink,
                    startedAtUtc,
                    stopwatch,
                    logicalModel,
                    StatusCodes.Status503ServiceUnavailable,
                    "token_budget_coordination_unavailable");
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "gateway_unavailable",
                    "token_budget_coordination_unavailable",
                    "Distributed output-token budget coordination is temporarily unavailable. Retry shortly.");
                return;
            }

            reservation = budgetDecision.Reservation;
            context.Items[ActiveReservationItem] = reservation;
            replacementBody = ReplaceBody(context, requestObject.ToJsonString());
            await next(context);
        }
        finally
        {
            if (reservation is not null)
            {
                await SettleAsync(context, reservation);
                context.Items.Remove(ActiveReservationItem);
            }

            context.Request.Body = originalBody;
            if (replacementBody is not null)
            {
                await replacementBody.DisposeAsync();
            }
        }
    }

    private static bool IsInferencePost(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
         request.Path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseObjectAndModel(string rawBody, out JsonObject requestObject, out string logicalModel)
    {
        requestObject = null!;
        logicalModel = string.Empty;
        try
        {
            requestObject = JsonNode.Parse(rawBody) as JsonObject ?? null!;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (requestObject is null ||
            requestObject["model"] is not JsonValue modelValue ||
            !modelValue.TryGetValue<string>(out var parsedModel) ||
            string.IsNullOrWhiteSpace(parsedModel))
        {
            return false;
        }

        logicalModel = parsedModel;
        return true;
    }

    private static MemoryStream ReplaceBody(HttpContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var stream = new MemoryStream(bytes, writable: false);
        context.Request.Body = stream;
        context.Request.ContentLength = bytes.Length;
        return stream;
    }

    private static async ValueTask SettleAsync(HttpContext context, IOutputTokenBudgetReservation reservation)
    {
        if (context.Items.TryGetValue(CapturedRequestMetricItem, out var metricValue) &&
            metricValue is GatewayRequestMetric metric)
        {
            if (metric.AttemptCount == 0)
            {
                await reservation.SettleAsync(0, usageCertain: true, CancellationToken.None);
                return;
            }

            if (metric.StatusCode is >= 200 and < 300 && metric.OutputTokens is int outputTokens)
            {
                await reservation.SettleAsync(outputTokens, usageCertain: true, CancellationToken.None);
                return;
            }
        }

        // Unknown/missing usage after upstream work keeps the full reservation charged.
        await reservation.SettleAsync(null, usageCertain: false, CancellationToken.None);
    }

    private static Task WriteRejectedMetricAsync(
        HttpContext context,
        IRequestMetricsSink metricsSink,
        DateTimeOffset startedAtUtc,
        Stopwatch stopwatch,
        string logicalModel,
        int statusCode,
        string errorCode)
    {
        var requestId = Guid.NewGuid();
        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
        var usageGroupId = context.Items.TryGetValue(InferenceApiKeyMiddleware.UsageGroupIdItem, out var groupValue) && groupValue is Guid groupId
            ? groupId
            : (Guid?)null;
        var apiCredentialId = context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var credentialValue) && credentialValue is Guid credentialId
            ? credentialId
            : (Guid?)null;
        var surface = context.Request.Path.Value?.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) == true
            ? "responses"
            : "chat_completions";

        metricsSink.Write(new GatewayRequestMetric(
            requestId,
            startedAtUtc,
            logicalModel,
            surface,
            null,
            null,
            apiCredentialId,
            usageGroupId,
            statusCode,
            stopwatch.ElapsedMilliseconds,
            0,
            false,
            null,
            null,
            null,
            null,
            null,
            errorCode));
        return Task.CompletedTask;
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string type, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type,
                code
            }
        });
    }
}

internal static class OutputTokenRequestPolicy
{
    public static bool TryApply(
        JsonObject requestObject,
        PathString path,
        int maxOutputTokensPerRequest,
        out int reservationTokens,
        out string errorCode,
        out string errorMessage)
    {
        reservationTokens = 0;
        errorCode = string.Empty;
        errorMessage = string.Empty;

        if (maxOutputTokensPerRequest < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokensPerRequest));
        }

        return path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase)
            ? ApplyResponses(requestObject, maxOutputTokensPerRequest, out reservationTokens, out errorCode, out errorMessage)
            : ApplyChat(requestObject, maxOutputTokensPerRequest, out reservationTokens, out errorCode, out errorMessage);
    }

    private static bool ApplyResponses(
        JsonObject requestObject,
        int maxPerRequest,
        out int reservationTokens,
        out string errorCode,
        out string errorMessage)
    {
        if (!TryReadAndCap(requestObject, "max_output_tokens", maxPerRequest, out var value, out var present))
        {
            return Invalid(out reservationTokens, out errorCode, out errorMessage, "max_output_tokens");
        }

        if (!present)
        {
            requestObject["max_output_tokens"] = maxPerRequest;
            value = maxPerRequest;
        }

        reservationTokens = value;
        errorCode = string.Empty;
        errorMessage = string.Empty;
        return true;
    }

    private static bool ApplyChat(
        JsonObject requestObject,
        int maxPerRequest,
        out int reservationTokens,
        out string errorCode,
        out string errorMessage)
    {
        if (!TryReadAndCap(requestObject, "max_completion_tokens", maxPerRequest, out var completionTokens, out var completionPresent))
        {
            return Invalid(out reservationTokens, out errorCode, out errorMessage, "max_completion_tokens");
        }

        if (!TryReadAndCap(requestObject, "max_tokens", maxPerRequest, out var legacyTokens, out var legacyPresent))
        {
            return Invalid(out reservationTokens, out errorCode, out errorMessage, "max_tokens");
        }

        if (!completionPresent && !legacyPresent)
        {
            requestObject["max_tokens"] = maxPerRequest;
            reservationTokens = maxPerRequest;
        }
        else
        {
            reservationTokens = Math.Max(completionPresent ? completionTokens : 0, legacyPresent ? legacyTokens : 0);
        }

        errorCode = string.Empty;
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryReadAndCap(
        JsonObject requestObject,
        string propertyName,
        int maxPerRequest,
        out int value,
        out bool present)
    {
        value = 0;
        present = requestObject.TryGetPropertyValue(propertyName, out var node) && node is not null;
        if (!present)
        {
            return true;
        }

        if (node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<int>(out var parsed) ||
            parsed < 1)
        {
            return false;
        }

        value = Math.Min(parsed, maxPerRequest);
        requestObject[propertyName] = value;
        return true;
    }

    private static bool Invalid(
        out int reservationTokens,
        out string errorCode,
        out string errorMessage,
        string propertyName)
    {
        reservationTokens = 0;
        errorCode = "invalid_output_token_limit";
        errorMessage = $"'{propertyName}' must be a positive integer when an output-token budget applies.";
        return false;
    }
}

public sealed class HttpContextRequestMetricsSink(
    BufferedRequestMetricsSink inner,
    IHttpContextAccessor httpContextAccessor) : IRequestMetricsSink
{
    public void Write(GatewayRequestMetric metric)
    {
        var context = httpContextAccessor.HttpContext;
        if (context?.Items.ContainsKey(OutputTokenBudgetMiddleware.ActiveReservationItem) == true)
        {
            context.Items[OutputTokenBudgetMiddleware.CapturedRequestMetricItem] = metric;
        }

        inner.Write(metric);
    }
}
