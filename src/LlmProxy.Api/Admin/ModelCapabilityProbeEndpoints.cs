using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;

namespace LlmProxy.Api.Admin;

public static class ModelCapabilityProbeEndpoints
{
    private static readonly TimeSpan PerCheckTimeout = TimeSpan.FromSeconds(120);

    public static IEndpointRouteBuilder MapModelCapabilityProbeEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var verify = endpoints.MapPost("/api/admin/model-management/deployments/{id:guid}/verify", async (
            Guid id, GatewayDbContext db, IHttpClientFactory factory, UpstreamCredentialProtector protector,
            HttpContext context, CancellationToken token) =>
        {
            var deployment = await db.Deployments.FindAsync([id], token);
            if (deployment is null) return Results.NotFound();
            var model = await db.Models.FindAsync([deployment.ModelId], token);
            var node = await db.Nodes.FindAsync([deployment.NodeId], token);
            if (model is null || node is null) return Results.NotFound();
            if (model.Surface != ModelSurface.OpenAi)
                return Results.BadRequest(new { error = "probe_only_for_chat_models",
                    message = "Capability checks exercise the OpenAI Chat endpoint; this model uses another surface." });

            var address = await ResolveRuntimeAddressAsync(deployment, node, factory, protector, token);
            if (address.Failure is not null) return address.Failure;

            var bearer = deployment.UpstreamBearerTokenCiphertext ?? node.UpstreamBearerTokenCiphertext;
            var client = factory.CreateClient("vllm");
            var provider = model.ProviderModelName;
            var started = Stopwatch.StartNew();

            var chat = await SendAsync(client, protector, bearer, address.Value!, ModelCapabilityProbe.ChatRequest(provider), token);
            var checks = new List<ProbeCheck>
            {
                chat.Failure ?? ModelCapabilityProbe.Chat(chat.Status, chat.Body),
                chat.Failure is null
                    ? ModelCapabilityProbe.Reasoning(chat.Status, chat.Body)
                    : new ProbeCheck("reasoning", ModelCapabilityProbe.Inconclusive, "Not checked: the chat request did not succeed.")
            };

            // Streaming and tools still run when chat failed: they may fail for a different, more specific reason.
            var stream = await SendAsync(client, protector, bearer, address.Value!,
                ModelCapabilityProbe.ChatRequest(provider, stream: true, maxTokens: 16), token);
            checks.Add(stream.Failure ?? ModelCapabilityProbe.Streaming(stream.Status, stream.ContentType, stream.Body));

            var tools = await SendAsync(client, protector, bearer, address.Value!, ModelCapabilityProbe.ToolRequest(provider), token);
            checks.Add(tools.Failure ?? ModelCapabilityProbe.Tools(tools.Status, tools.Body));

            var result = new { deploymentId = deployment.Id, model = model.PublicName, checkedAtUtc = DateTimeOffset.UtcNow,
                durationMilliseconds = started.ElapsedMilliseconds, checks };
            db.AuditEvents.Add(new AuditEvent(
                context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "local-admin",
                "model.verify", "deployment", deployment.Id.ToString(),
                context.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { statuses = checks.ToDictionary(check => check.Id, check => check.Status) })));
            await db.SaveChangesAsync(token);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result);
        });

        if (entraEnabled) verify.RequireAuthorization("AdminWrite");
        return endpoints;
    }

    private sealed record Resolved(string? Value, IResult? Failure);

    private static async Task<Resolved> ResolveRuntimeAddressAsync(
        ModelDeployment deployment, InferenceNode node, IHttpClientFactory factory,
        UpstreamCredentialProtector protector, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(deployment.ManagedInstallationId))
        {
            var manual = deployment.RuntimeBaseAddress ?? node.BaseAddress;
            return new(manual, null);
        }

        if (string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
            return new(null, Results.Conflict(new { error = "management_agent_not_configured" }));
        try
        {
            var inventory = await NodeModelManagementEndpoints.SendAgentAsync<NodeModelManagementEndpoints.ManagedModelsResponse>(
                factory.CreateClient("node-management"), node, protector, HttpMethod.Get, "/v1/models", null, token);
            var state = inventory.Models?.SingleOrDefault(row =>
                string.Equals(row.InstallationId, deployment.ManagedInstallationId, StringComparison.Ordinal));
            if (state is null)
                return new(null, Results.Conflict(new { error = "installation_missing",
                    message = "The Agent no longer has this installation." }));
            if (!NodeModelManagementEndpoints.IsAgentRuntimeRunnable(state))
                return new(null, Results.Conflict(new { error = "runtime_not_running",
                    message = "The runtime is not running. Start the model before verifying it." }));
            var address = NodeModelManagementEndpoints.OutboundRuntimeAddress(node, state);
            return string.IsNullOrWhiteSpace(address)
                ? new(null, Results.Conflict(new { error = "runtime_address_missing" }))
                : new(address, null);
        }
        catch (NodeModelManagementEndpoints.AgentException error)
        {
            return new(null, Results.Problem(error.Message, statusCode: StatusCodes.Status502BadGateway));
        }
    }

    private sealed record ProbeResponse(int Status, string ContentType, string Body, ProbeCheck? Failure);

    private static async Task<ProbeResponse> SendAsync(
        HttpClient client, UpstreamCredentialProtector protector, string? bearer, string runtimeAddress,
        JsonObject body, CancellationToken token)
    {
        var id = body["stream"]?.GetValue<bool>() == true ? "streaming" : body["tools"] is not null ? "tools" : "chat";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(PerCheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                InferenceEndpoint.Combine(runtimeAddress, "/v1/chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            protector.ApplyBearer(request, bearer);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            // A streamed answer is read to its end only up to a small bound: the probe asks for a few tokens.
            var text = await ReadBoundedAsync(response, 256 * 1024, timeout.Token);
            return new((int)response.StatusCode, contentType, text, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(0, string.Empty, string.Empty, new ProbeCheck(id, ModelCapabilityProbe.Failed,
                $"No answer within {(int)PerCheckTimeout.TotalSeconds} s."));
        }
        catch (HttpRequestException)
        {
            return new(0, string.Empty, string.Empty, new ProbeCheck(id, ModelCapabilityProbe.Failed,
                "The runtime could not be reached."));
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while (buffer.Length < maxBytes && (read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
