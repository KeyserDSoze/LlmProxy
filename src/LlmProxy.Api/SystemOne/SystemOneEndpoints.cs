using System.Net.Http.Headers;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Api.SystemOne;

public static class SystemOneEndpoints
{
    private const string SystemOnePath = "/v1/systemone";

    public static IEndpointRouteBuilder MapSystemOneEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(SystemOnePath, ForwardAsync);
        return endpoints;
    }

    private static async Task ForwardAsync(
        HttpContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        if (!configuration.GetValue<bool>("SystemOne:Enabled"))
        {
            await WriteGatewayErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "classifier_unavailable",
                "The System One classifier endpoint is not enabled.");
            return;
        }

        var baseAddress = configuration["SystemOne:BaseAddress"];
        if (string.IsNullOrWhiteSpace(baseAddress))
        {
            await WriteGatewayErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "classifier_not_configured",
                "The System One classifier upstream is not configured.");
            return;
        }

        Uri destination;
        try
        {
            destination = InferenceEndpoint.Combine(baseAddress, SystemOnePath);
        }
        catch (ArgumentException)
        {
            await WriteGatewayErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "classifier_not_configured",
                "The System One classifier upstream address is invalid.");
            return;
        }

        var requestId = Guid.NewGuid();
        context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();

        using var outbound = new HttpRequestMessage(HttpMethod.Post, destination);
        using var content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
        {
            content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }

        if (context.Request.ContentLength is long contentLength)
        {
            content.Headers.ContentLength = contentLength;
        }

        outbound.Content = content;
        outbound.Headers.TryAddWithoutValidation("X-LlmProxy-Request-Id", requestId.ToString());

        if (context.Request.Headers.TryGetValue("Accept", out var accept))
        {
            outbound.Headers.TryAddWithoutValidation("Accept", accept.ToArray());
        }

        var upstreamApiKey = configuration["SystemOne:ApiKey"];
        if (!string.IsNullOrWhiteSpace(upstreamApiKey))
        {
            outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstreamApiKey.Trim());
        }

        var client = httpClientFactory.CreateClient("system-one");

        try
        {
            using var upstream = await client.SendAsync(
                outbound,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted);

            context.Response.StatusCode = (int)upstream.StatusCode;
            CopyResponseHeaders(upstream, context.Response);

            await using var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted);
            await body.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
                await WriteGatewayErrorAsync(
                    context,
                    StatusCodes.Status504GatewayTimeout,
                    "classifier_timeout",
                    "The System One classifier upstream timed out.");
            }
        }
        catch (HttpRequestException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.Headers["X-LlmProxy-Request-Id"] = requestId.ToString();
                await WriteGatewayErrorAsync(
                    context,
                    StatusCodes.Status502BadGateway,
                    "classifier_unreachable",
                    "The System One classifier upstream could not be reached.");
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var header in source.Headers)
        {
            if (!IsHopByHopHeader(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in source.Content.Headers)
        {
            if (!IsHopByHopHeader(header.Key))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }

        destination.Headers.Remove("transfer-encoding");
    }

    private static bool IsHopByHopHeader(string name) =>
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static Task WriteGatewayErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type = "gateway_error",
                code
            }
        });
    }
}
