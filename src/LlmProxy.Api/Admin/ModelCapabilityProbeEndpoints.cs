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
            Guid id, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            var outcome = await VerifyAsync(services, id, ManagedModelOperations.Caller.From(context), token);
            context.Response.Headers.CacheControl = "no-store";
            return outcome.Failure is null ? Results.Ok(outcome.Result) : outcome.Failure.ToResult();
        });

        // Four probes of up to two minutes each exceed what a browser or proxy waits for, so the checks run in the
        // background and the result is read from /api/admin/operations/{id}.
        var verifyJob = endpoints.MapPost("/api/admin/model-management/deployments/{id:guid}/verify-jobs", async (
            Guid id, GatewayDbContext db, AdminOperationService operations, HttpContext context) =>
        {
            var deployment = await db.Deployments.FindAsync([id], context.RequestAborted);
            if (deployment is null) return Results.NotFound();
            var caller = ManagedModelOperations.Caller.From(context);
            var operation = operations.Start("model.verify", "Verify capabilities · " + (deployment.ManagedInstallationId ?? deployment.Id.ToString()),
                deployment.NodeId, id, async (services, token) =>
                {
                    var outcome = await VerifyAsync(services, id, caller, token);
                    return outcome.Failure is null
                        ? new AdminOperationOutcome(true, "Checks finished.", outcome.Result)
                        : new AdminOperationOutcome(false, outcome.Failure.Message);
                });
            return operation is null
                ? Results.Conflict(new { error = "operation_in_progress", message = "Another operation is already running for this model." })
                : Results.Accepted($"/api/admin/operations/{operation.Id}", operation);
        });

        if (entraEnabled)
        {
            verify.RequireAuthorization("AdminWrite");
            verifyJob.RequireAuthorization("AdminWrite");
        }
        return endpoints;
    }

    internal sealed record ProbeFailure(int Status, string Error, string Message)
    {
        public IResult ToResult() => Results.Json(new { error = Error, message = Message }, statusCode: Status);
    }

    private sealed record VerifyOutcome(object? Result, ProbeFailure? Failure);

    private static async Task<VerifyOutcome> VerifyAsync(
        IServiceProvider services, Guid id, ManagedModelOperations.Caller caller, CancellationToken token)
    {
        var db = services.GetRequiredService<GatewayDbContext>();
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var protector = services.GetRequiredService<UpstreamCredentialProtector>();
        var deployment = await db.Deployments.FindAsync([id], token);
        if (deployment is null) return new(null, new(404, "not_found", "The deployment no longer exists."));
        var model = await db.Models.FindAsync([deployment.ModelId], token);
        var node = await db.Nodes.FindAsync([deployment.NodeId], token);
        if (model is null || node is null) return new(null, new(404, "not_found", "The model or server no longer exists."));
        if (model.Surface != ModelSurface.OpenAi)
            return new(null, new(400, "probe_only_for_chat_models",
                "Capability checks exercise the OpenAI Chat endpoint; this model uses another surface."));

        var address = await ResolveRuntimeAddressAsync(deployment, node, factory, protector, token);
        if (address.Failure is not null) return new(null, address.Failure);

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
        db.AuditEvents.Add(new AuditEvent(caller.Actor, "model.verify", "deployment", deployment.Id.ToString(), caller.SourceIp,
            JsonSerializer.Serialize(new { statuses = checks.ToDictionary(check => check.Id, check => check.Status) })));
        await db.SaveChangesAsync(token);
        return new(result, null);
    }

    private sealed record Resolved(string? Value, ProbeFailure? Failure);

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
            return new(null, new(409, "management_agent_not_configured", "No management Agent is configured for this server."));
        try
        {
            var inventory = await NodeModelManagementEndpoints.SendAgentAsync<NodeModelManagementEndpoints.ManagedModelsResponse>(
                factory.CreateClient("node-management"), node, protector, HttpMethod.Get, "/v1/models", null, token);
            var state = inventory.Models?.SingleOrDefault(row =>
                string.Equals(row.InstallationId, deployment.ManagedInstallationId, StringComparison.Ordinal));
            if (state is null)
                return new(null, new(409, "installation_missing", "The Agent no longer has this installation."));
            if (!NodeModelManagementEndpoints.IsAgentRuntimeRunnable(state))
                return new(null, new(409, "runtime_not_running", "The runtime is not running. Start the model before verifying it."));
            var address = NodeModelManagementEndpoints.OutboundRuntimeAddress(node, state);
            return string.IsNullOrWhiteSpace(address)
                ? new(null, new(409, "runtime_address_missing", "The Agent did not report a runtime address."))
                : new(address, null);
        }
        catch (NodeModelManagementEndpoints.AgentException error)
        {
            return new(null, new(502, "agent_unreachable", error.Message));
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
