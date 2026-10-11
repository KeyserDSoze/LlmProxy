using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.Multimodal;

/// <summary>
/// OpenAI-compatible multimodal gateway for ALREADY-RUNNING OpenAI-compatible
/// inference services. Installation/adapter certification is a separate Agent
/// lifecycle operation. Never route one modality to another's deployments.
/// </summary>
public static class MultimodalEndpoints
{
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxAudioUploadBytes = 25 * 1024 * 1024;
    private const int MaxFormFields = 24;

    private sealed record Endpoint(string Path, ModelSurface Surface, string Metric, bool Multipart, int TimeoutMinutes);
    private static readonly Endpoint Image = new("/v1/images/generations", ModelSurface.ImageGeneration, "image_generation", false, 20);
    private static readonly Endpoint Speech = new("/v1/audio/speech", ModelSurface.SpeechSynthesis, "audio_speech", false, 10);
    private static readonly Endpoint Transcription = new("/v1/audio/transcriptions", ModelSurface.AudioTranscription, "audio_transcription", true, 20);
    private static readonly Endpoint Translation = new("/v1/audio/translations", ModelSurface.AudioTranscription, "audio_translation", true, 20);

    public static IEndpointRouteBuilder MapMultimodalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Image.Path, (HttpContext context, RoutingService routing,
            IRequestCapacityGate capacity, RequestRateLimiter limiter, IRequestMetricsSink metrics,
            IHttpClientFactory clients, UpstreamCredentialProtector credentials) =>
            ForwardAsync(context, Image, routing, capacity, limiter, metrics, clients, credentials));
        endpoints.MapPost(Speech.Path, (HttpContext context, RoutingService routing,
            IRequestCapacityGate capacity, RequestRateLimiter limiter, IRequestMetricsSink metrics,
            IHttpClientFactory clients, UpstreamCredentialProtector credentials) =>
            ForwardAsync(context, Speech, routing, capacity, limiter, metrics, clients, credentials));
        endpoints.MapPost(Transcription.Path, (HttpContext context, RoutingService routing,
            IRequestCapacityGate capacity, RequestRateLimiter limiter, IRequestMetricsSink metrics,
            IHttpClientFactory clients, UpstreamCredentialProtector credentials) =>
            ForwardAsync(context, Transcription, routing, capacity, limiter, metrics, clients, credentials));
        endpoints.MapPost(Translation.Path, (HttpContext context, RoutingService routing,
            IRequestCapacityGate capacity, RequestRateLimiter limiter, IRequestMetricsSink metrics,
            IHttpClientFactory clients, UpstreamCredentialProtector credentials) =>
            ForwardAsync(context, Translation, routing, capacity, limiter, metrics, clients, credentials));
        return endpoints;
    }

    private static async Task ForwardAsync(HttpContext context, Endpoint endpoint,
        RoutingService routing, IRequestCapacityGate capacity, RequestRateLimiter limiter,
        IRequestMetricsSink metrics, IHttpClientFactory clients,
        UpstreamCredentialProtector credentials)
    {
        var requestId = Guid.NewGuid();
        var began = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var credentialId = context.Items.TryGetValue(InferenceApiKeyMiddleware.ApiCredentialIdItem, out var credential)
            && credential is Guid value ? value : (Guid?)null;
        var groupId = context.Items.TryGetValue(InferenceApiKeyMiddleware.UsageGroupIdItem, out var group)
            && group is Guid id ? id : (Guid?)null;
        var tenant = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerTenantIdItem, out var t) ? t as string : null;
        var owner = context.Items.TryGetValue(InferenceApiKeyMiddleware.OwnerObjectIdItem, out var o) ? o as string : null;
        var enforce = context.Items.TryGetValue(InferenceApiKeyMiddleware.EnforceCallerGovernanceItem, out var e) && e is true;
        Guid? deploymentId = null, nodeId = null;
        long? headersMilliseconds = null;
        var status = 500;
        var attempt = 0;
        var stream = false;
        string? error = null;
        string? model = null;

        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
        try
        {
            // The shape of the body is different for each OpenAI endpoint.
            // Multipart is rebuilt to substitute the PUBLIC alias with the
            // provider model, preserving file binary bytes and field names.
            JsonObject? json = null;
            IFormCollection? form = null;
            if (endpoint.Multipart)
            {
                if (!context.Request.HasFormContentType)
                {
                    await FailAsync(context, 415, "unsupported_media_type",
                        "Speech transcription requires multipart/form-data.");
                    status = 415; error = "unsupported_media_type"; return;
                }
                var bytes = await ReadBoundedAsync(context.Request.Body, MaxAudioUploadBytes, context.RequestAborted);
                context.Request.Body = new MemoryStream(bytes, writable: false);
                context.Request.ContentLength = bytes.Length;
                form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (form.Count > MaxFormFields || form.Files.Count != 1 || form.Files[0].Length == 0)
                {
                    status = 400; error = "invalid_audio_upload";
                    await FailAsync(context, status, error, "Exactly one non-empty audio file and no more than 24 form fields are required.");
                    return;
                }
                model = form["model"].ToString();
            }
            else
            {
                if (!context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    status = 415; error = "unsupported_media_type";
                    await FailAsync(context, status, error, "This endpoint requires application/json.");
                    return;
                }
                var raw = await ReadBoundedAsync(context.Request.Body, MaxJsonBytes, context.RequestAborted);
                try { json = JsonNode.Parse(raw)?.AsObject(); }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                {
                    status = 400; error = "invalid_json";
                    await FailAsync(context, status, error, "The request body must be a JSON object.");
                    return;
                }
                if (json is null)
                {
                    status = 400; error = "invalid_json";
                    await FailAsync(context, status, error, "The request body must be a JSON object.");
                    return;
                }
                if (json["model"] is JsonValue name && name.TryGetValue<string>(out var selected))
                    model = selected;
                // Avoid accidentally replaying an unsupported body or accepting
                // arbitrary base64 image editing on a generation-only route.
                if (endpoint == Image && (json["prompt"] is not JsonValue prompt ||
                    !prompt.TryGetValue<string>(out var promptText) || string.IsNullOrWhiteSpace(promptText)))
                {
                    status = 400; error = "invalid_prompt";
                    await FailAsync(context, status, error, "A non-empty prompt is required.");
                    return;
                }
                if (endpoint == Speech && (json["input"] is not JsonValue input ||
                    !input.TryGetValue<string>(out var speechText) || string.IsNullOrWhiteSpace(speechText) ||
                    speechText.Length > 4096 || json["voice"] is null))
                {
                    status = 400; error = "invalid_speech_request";
                    await FailAsync(context, status, error, "Speech requests require input (up to 4096 characters) and a voice.");
                    return;
                }
            }
            model = model?.Trim();
            if (string.IsNullOrWhiteSpace(model) || model.Length > 160)
            {
                status = 400; error = "model_required";
                await FailAsync(context, status, error, "A valid logical model name is required.");
                return;
            }

            if (credentialId is Guid caller)
            {
                var decision = await limiter.TryAcquireAsync(caller, model, tenant, owner,
                    groupId, enforce, DateTimeOffset.UtcNow, context.RequestAborted);
                if (!decision.Allowed)
                {
                    status = 429; error = "rate_limit_exceeded";
                    context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    await FailAsync(context, status, error, "Request rate limit exceeded.");
                    return;
                }
            }

            var selection = await routing.SelectDetailedAsync(model, endpoint.Surface,
                excludedDeploymentIds: null, context.RequestAborted);
            if (selection.Route is not { } route)
            {
                status = selection.Failure == RoutingSelectionFailure.CapacityExhausted ? 429 : 503;
                error = status == 429 ? "capacity_exhausted" : "no_healthy_deployment";
                await FailAsync(context, status, error, $"No healthy {endpoint.Metric} deployment is available for '{model}'.");
                return;
            }
            deploymentId = route.DeploymentId;
            nodeId = route.NodeId;
            var admission = await capacity.TryAcquireAsync(route.DeploymentId, route.NodeId,
                route.MaxConcurrency, route.NodeMaxConcurrency, context.RequestAborted);
            if (!admission.Acquired || admission.Lease is null)
            {
                status = admission.Failure == CapacityAdmissionFailure.CoordinationUnavailable ? 503 : 429;
                error = status == 429 ? "capacity_exhausted" : "capacity_coordination_unavailable";
                await FailAsync(context, status, error, "Inference capacity is unavailable.");
                return;
            }

            await using var lease = admission.Lease;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.CoordinationLost);
            linked.CancelAfter(TimeSpan.FromMinutes(endpoint.TimeoutMinutes));
            using var outbound = new HttpRequestMessage(HttpMethod.Post,
                InferenceEndpoint.Combine(route.BaseAddress, endpoint.Path));
            if (form is not null)
            {
                var multipart = new MultipartFormDataContent();
                foreach (var field in form)
                {
                    if (field.Key == "model")
                        multipart.Add(new StringContent(route.ProviderModelName), "model");
                    else foreach (var fieldValue in field.Value)
                        multipart.Add(new StringContent(fieldValue ?? ""), field.Key);
                }
                if (!form.ContainsKey("model"))
                    multipart.Add(new StringContent(route.ProviderModelName), "model");
                var file = form.Files[0];
                var fileContent = new StreamContent(file.OpenReadStream());
                if (MediaTypeHeaderValue.TryParse(file.ContentType, out var mediaType))
                    fileContent.Headers.ContentType = mediaType;
                multipart.Add(fileContent, file.Name, Path.GetFileName(file.FileName));
                outbound.Content = multipart;
            }
            else
            {
                json!["model"] = route.ProviderModelName;
                outbound.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
            }
            if (context.Request.Headers.TryGetValue("Accept", out var accepts))
                outbound.Headers.TryAddWithoutValidation("Accept", accepts.ToArray());
            outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());
            credentials.ApplyBearer(outbound, route.UpstreamBearerTokenCiphertext);
            // Do not automatically retry non-idempotent image/audio requests:
            // the first upstream could already have generated or billed output.
            attempt = 1;
            var watch = Stopwatch.StartNew();
            using var upstream = await clients.CreateClient("multimodal")
                .SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            headersMilliseconds = watch.ElapsedMilliseconds;
            status = (int)upstream.StatusCode;
            error = upstream.IsSuccessStatusCode ? null : "upstream_error";
            context.Response.StatusCode = status;
            if (upstream.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            if (upstream.Content.Headers.ContentDisposition is { } contentDisposition)
                context.Response.Headers.ContentDisposition = contentDisposition.ToString();
            if (upstream.Headers.TryGetValues("Cache-Control", out var cache))
                context.Response.Headers.CacheControl = string.Join(", ", cache);
            stream = context.Response.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
            await using var upstreamBody = await upstream.Content.ReadAsStreamAsync(linked.Token);
            await upstreamBody.CopyToAsync(context.Response.Body, linked.Token);
        }
        catch (InvalidDataException)
        {
            if (!context.Response.HasStarted)
            {
                status = 413; error = "payload_too_large";
                await FailAsync(context, status, error, "Request payload exceeds the configured multimodal size limit.");
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            status = 499; error = "client_cancelled";
        }
        catch (OperationCanceledException)
        {
            status = leaseFailure(context) ? 503 : 504;
            error = status == 503 ? "capacity_coordination_unavailable" : "upstream_timeout";
            if (!context.Response.HasStarted)
                await FailAsync(context, status, error, "The multimodal inference request did not complete.");
            else context.Abort();
        }
        catch (HttpRequestException)
        {
            status = 502; error = "upstream_unreachable";
            if (!context.Response.HasStarted)
                await FailAsync(context, status, error, "The selected runtime could not be reached.");
            else context.Abort();
        }
        finally
        {
            metrics.Write(new GatewayRequestMetric(requestId, began, model ?? endpoint.Metric,
                endpoint.Metric, deploymentId, nodeId, credentialId, groupId, status,
                stopwatch.ElapsedMilliseconds, attempt, stream, headersMilliseconds,
                null, null, null, null, error));
        }
    }

    // The coordination lease is linked into the outbound timeout token. Either
    // cause becomes a 503/504, never a silent partial retry.
    private static bool leaseFailure(HttpContext context) =>
        context.RequestAborted.IsCancellationRequested;

    public static async Task<byte[]> ReadBoundedAsync(Stream body, int maxBytes, CancellationToken token)
    {
        await using var data = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var count = await body.ReadAsync(buffer, token);
            if (count == 0) return data.ToArray();
            if (data.Length + count > maxBytes)
                throw new InvalidDataException("Multipart or JSON request exceeds configured limit.");
            await data.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private static Task FailAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = new { message, type = "gateway_error", code }
        });
    }
}
