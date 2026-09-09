using System.Text;
using System.Text.Json.Nodes;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;

namespace LlmProxy.Api.OpenAi;

public static class OpenAiEndpoints
{
    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/models", async (IDeploymentCatalog catalog, CancellationToken cancellationToken) =>
        {
            var models = await catalog.GetPublicModelsAsync(cancellationToken);
            return Results.Ok(new
            {
                @object = "list",
                data = models.Select(model => new
                {
                    id = model.PublicName,
                    @object = "model",
                    created = 0,
                    owned_by = "llmproxy"
                })
            });
        });

        endpoints.MapPost("/v1/chat/completions", ForwardChatCompletionsAsync);
        return endpoints;
    }

    private static async Task ForwardChatCompletionsAsync(
        HttpContext context,
        RoutingService routingService,
        IRequestLoadTracker loadTracker,
        IHttpClientFactory httpClientFactory)
    {
        string rawBody;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(context.RequestAborted);
        }

        JsonObject? requestObject;
        try
        {
            requestObject = JsonNode.Parse(rawBody) as JsonObject;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or FormatException)
        {
            await WriteGatewayErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", "invalid_json", "Request body is not valid JSON.");
            return;
        }

        if (requestObject is null || requestObject["model"] is not JsonValue modelValue || !modelValue.TryGetValue<string>(out var publicModelName) || string.IsNullOrWhiteSpace(publicModelName))
        {
            await WriteGatewayErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request_error", "model_required", "A logical model name is required.");
            return;
        }

        var route = await routingService.SelectAsync(publicModelName, context.RequestAborted);
        if (route is null)
        {
            await WriteGatewayErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "gateway_unavailable",
                "no_healthy_deployment",
                $"No healthy deployment is available for model '{publicModelName}'.");
            return;
        }

        requestObject["model"] = route.ProviderModelName;
        using var lease = loadTracker.Enter(route.DeploymentId);
        using var outbound = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{route.BaseAddress.TrimEnd('/')}/v1/chat/completions"));

        outbound.Content = new StringContent(requestObject.ToJsonString(), Encoding.UTF8, "application/json");
        CopyRequestHeaders(context.Request, outbound);

        var client = httpClientFactory.CreateClient("vllm");

        try
        {
            using var upstream = await client.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)upstream.StatusCode;
            CopyResponseHeaders(upstream, context.Response);
            await upstream.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (HttpRequestException exception)
        {
            if (!context.Response.HasStarted)
            {
                await WriteGatewayErrorAsync(
                    context,
                    StatusCodes.Status502BadGateway,
                    "gateway_error",
                    "upstream_unreachable",
                    $"Inference runtime '{route.NodeName}' could not be reached: {exception.Message}");
            }
        }
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (var header in source.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!destination.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                destination.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers)
        {
            if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in source.Content.Headers)
        {
            if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        destination.Headers.Remove("transfer-encoding");
    }

    private static Task WriteGatewayErrorAsync(HttpContext context, int statusCode, string type, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
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
