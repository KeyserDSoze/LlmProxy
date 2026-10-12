using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.Admin;

/// <summary>
/// A diagnostic SSE wrapper for slow, non-streaming model inference.
/// Progress events are Gateway waiting heartbeats, NEVER generated model tokens.
/// This keeps the Cloudflare origin response active while AirLLM runs.
/// </summary>
public static class PlaygroundChatStreamEndpoints
{
    private static readonly TimeSpan Pulse = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(8);

    public static IEndpointRouteBuilder MapPlaygroundChatStreamEndpoints(
        this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/testing");
        if (entraEnabled) group.RequireAuthorization("AdminWrite");

        group.MapPost("/chat/stream", async Task<IResult> (
            TestingAdminEndpoints.ChatModelTestRequest request,
            RoutingService routingService,
            IRequestCapacityGate capacityGate,
            IHttpClientFactory clients,
            UpstreamCredentialProtector credentialProtector,
            IInferenceContentLogSink logSink,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Model) ||
                string.IsNullOrWhiteSpace(request.UserPrompt) ||
                request.UserPrompt.Length > 32000 ||
                request.SystemPrompt?.Length > 8000)
                return Results.BadRequest(new { error = "invalid_chat_test",
                    message = "Select a model and provide a prompt (32k characters maximum)." });

            var logicalModel = request.Model.Trim();
            if (logicalModel.Length > 160)
                return Results.BadRequest(new { error = "invalid_model" });

            var requestId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();

            // A fast diagnostic should not silently request hundreds of expensive
            // AirLLM layer-streamed tokens. The caller can explicitly select up to 256.
            var maxTokens = Math.Clamp(request.MaxTokens ?? 16, 1, 256);
            var temperature = Math.Clamp(request.Temperature ?? 0.0, 0.0, 2.0);

            var selected = await routingService.SelectDetailedAsync(
                logicalModel, ModelSurface.OpenAi, null, cancellationToken);
            if (selected.Route is not { } route)
                return Results.Json(new {
                    error = selected.Failure == RoutingSelectionFailure.CapacityExhausted
                        ? "capacity_exhausted" : "no_healthy_deployment",
                    message = "No eligible model deployment. Open routing diagnostics to identify the missing step.",
                    model = logicalModel
                }, statusCode: selected.Failure == RoutingSelectionFailure.CapacityExhausted ? 429 : 503);

            var admission = await capacityGate.TryAcquireAsync(
                route.DeploymentId, route.NodeId, route.MaxConcurrency,
                route.NodeMaxConcurrency, cancellationToken);
            if (!admission.Acquired || admission.Lease is null)
                return Results.Json(new {
                    error = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable
                        ? "capacity_coordination_unavailable" : "capacity_exhausted",
                    message = "This model is busy or capacity coordination is unavailable. Try again after the active inference completes."
                }, statusCode: admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable ? 503 : 429);

            await using var lease = admission.Lease;
            using var timeout = new CancellationTokenSource(MaximumWait);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lease.CoordinationLost, timeout.Token);

            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
                messages.Add(new { role = "system", content = request.SystemPrompt });
            messages.Add(new { role = "user", content = request.UserPrompt });

            var payload = JsonSerializer.Serialize(new {
                model = route.ProviderModelName, messages,
                stream = false, max_tokens = maxTokens, temperature
            });

            var statusCode = 502;
            var responseBody = string.Empty;
            string? responseContentType = null;

            httpContext.Response.StatusCode = 200;
            httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-store, no-transform";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
            // Deliver bytes *before* the long model call. Heartbeats refresh
            // Cloudflare's origin idle timeout, unlike a buffered JSON response.
            await WriteEventAsync(httpContext.Response, "progress",
                new { phase = "starting", secondsElapsed = 0, requestId }, cancellationToken);

            try
            {
                using var outbound = new HttpRequestMessage(HttpMethod.Post,
                    InferenceEndpoint.Combine(route.BaseAddress, "/v1/chat/completions")) {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());
                credentialProtector.ApplyBearer(outbound, route.UpstreamBearerTokenCiphertext);

                // Non-streaming AirLLM only responds after generation. We keep
                // the admin diagnostic alive with *waiting* heartbeats, not fake SSE.
                var upstreamTask = clients.CreateClient("vllm")
                    .SendAsync(outbound, HttpCompletionOption.ResponseContentRead, linked.Token);
                while (!upstreamTask.IsCompleted)
                {
                    var next = await Task.WhenAny(upstreamTask, Task.Delay(Pulse, linked.Token));
                    if (next == upstreamTask) break;
                    linked.Token.ThrowIfCancellationRequested();
                    await WriteEventAsync(httpContext.Response, "progress", new {
                        phase = "waiting_for_model", secondsElapsed = (int)elapsed.Elapsed.TotalSeconds,
                        requestId
                    }, cancellationToken);
                }

                using var upstream = await upstreamTask;
                statusCode = (int)upstream.StatusCode;
                responseContentType = upstream.Content.Headers.ContentType?.ToString();
                responseBody = await upstream.Content.ReadAsStringAsync(linked.Token);
                await WriteEventAsync(httpContext.Response, "result", new {
                    requestId, success = upstream.IsSuccessStatusCode, statusCode,
                    latencyMilliseconds = elapsed.ElapsedMilliseconds, logicalModel,
                    providerModel = route.ProviderModelName, route.DeploymentId,
                    route.NodeId, route.NodeName, requestBody = payload,
                    responseContentType, responseBody
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Client closed the tab. Do not attempt to write on a closed socket.
                statusCode = 499;
                responseBody = """{"error":"client_disconnected"}""";
            }
            catch (OperationCanceledException) when (lease.CoordinationLost.IsCancellationRequested)
            {
                statusCode = 503;
                responseBody = """{"error":"capacity_lease_lost"}""";
                await WriteFailureAsync(httpContext.Response, requestId, logicalModel,
                    route.NodeName, "capacity_lease_lost",
                    "Inference stopped because the capacity reservation was lost.",
                    statusCode, elapsed.ElapsedMilliseconds, cancellationToken);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                statusCode = 504;
                responseBody = """{"error":"inference_timeout"}""";
                await WriteFailureAsync(httpContext.Response, requestId, logicalModel,
                    route.NodeName, "inference_timeout",
                    "AirLLM did not finish within 8 minutes. Try 1–8 output tokens, inspect the runtime logs and measure token latency.",
                    statusCode, elapsed.ElapsedMilliseconds, cancellationToken);
            }
            catch (HttpRequestException)
            {
                statusCode = 502;
                responseBody = """{"error":"upstream_unreachable"}""";
                await WriteFailureAsync(httpContext.Response, requestId, logicalModel,
                    route.NodeName, "upstream_unreachable",
                    "The outbound Agent tunnel or model server could not complete the request.",
                    statusCode, elapsed.ElapsedMilliseconds, cancellationToken);
            }
            finally
            {
                logSink.Write(new InferenceContentLog(
                    requestId, startedAt, DateTimeOffset.UtcNow, "model_test",
                    HttpMethods.Post, "/api/admin/testing/chat/stream", logicalModel,
                    null, statusCode, "application/json",
                    responseContentType ?? "application/json", payload, responseBody));
            }
            return Results.Empty;
        });

        return endpoints;
    }

    internal static string FormatEvent(string eventName, object payload) =>
        "event: " + eventName + "\ndata: " + JsonSerializer.Serialize(payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n\n";

    private static async Task WriteEventAsync(HttpResponse response, string eventName,
        object payload, CancellationToken token)
    {
        await response.WriteAsync(FormatEvent(eventName, payload), token);
        await response.Body.FlushAsync(token);
    }

    private static Task WriteFailureAsync(HttpResponse response, Guid requestId,
        string model, string nodeName, string error, string message,
        int statusCode, long milliseconds, CancellationToken token) =>
        WriteEventAsync(response, "result", new {
            requestId, success = false, statusCode, latencyMilliseconds = milliseconds,
            logicalModel = model, nodeName, error, message,
            responseBody = JsonSerializer.Serialize(new { error, message })
        }, token);
}
