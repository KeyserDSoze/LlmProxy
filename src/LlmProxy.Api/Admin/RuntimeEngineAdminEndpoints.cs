using System.Text.Json;
using LlmProxy.Api.Admin;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

internal sealed record AgentEngine(string Id, string Label, string Description, string Source, string Image, bool Installed,
    string? Version, string? Revision, string? ImageId, DateTimeOffset? CreatedAtUtc, long? SizeBytes,
    int RunningContainers, int Installations, string[] Serves);

internal sealed record AgentEngineJob(Guid Id, string EngineId, string Action, string Status, string Detail, string? Error,
    JsonElement? Result, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc);

internal sealed record AgentEngineCheck(string Id, string Status, string? Detail);

/// <summary>
/// Serving engines per server: what is installed and at which version, install / update / uninstall, and an optional
/// automatic update at a chosen time. The Agent does the Docker work; the gateway keeps the schedule (PostgreSQL), so it
/// survives restarts and is shared by every replica.
/// </summary>
public static class RuntimeEngineAdminEndpoints
{
    public sealed record EngineJobRequest(string Action);
    public sealed record EnginePolicyRequest(bool AutoUpdate, string UpdateAtLocal, string TimeZone);

    public static IEndpointRouteBuilder MapRuntimeEngineAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/engines");
        if (entraEnabled) group.RequireAuthorization("AdminRead");

        group.MapGet("/nodes/{id:guid}", async (Guid id, GatewayDbContext db, IHttpClientFactory factory,
            UpstreamCredentialProtector protector, CancellationToken token) =>
        {
            var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            if (node is null) return Results.NotFound();
            var policies = (await db.RuntimeEnginePolicies.AsNoTracking().Where(policy => policy.NodeId == id).ToListAsync(token))
                .ToDictionary(policy => policy.EngineId);
            if (string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
                return Results.Ok(new { agentConfigured = false, agentAvailable = false, agentError = (string?)null, engines = Array.Empty<object>(), jobs = Array.Empty<object>() });
            try
            {
                var client = factory.CreateClient("node-management");
                var engines = await NodeModelManagementEndpoints.SendAgentAsync<IReadOnlyList<AgentEngine>>(
                    client, node, protector, HttpMethod.Get, "/v1/runtime-engines", null, token);
                var jobs = await NodeModelManagementEndpoints.SendAgentAsync<IReadOnlyList<AgentEngineJob>>(
                    client, node, protector, HttpMethod.Get, "/v1/runtime-engines/jobs", null, token);
                return Results.Ok(new
                {
                    agentConfigured = true, agentAvailable = true, agentError = (string?)null,
                    engines = engines.Select(engine => new { engine, policy = PolicyView(policies.GetValueOrDefault(engine.Id)) }),
                    jobs
                });
            }
            catch (NodeModelManagementEndpoints.AgentException error)
            {
                var message = error.StatusCode == StatusCodes.Status404NotFound
                    ? "This server's Agent is too old to manage engines. Upgrade the Agent first."
                    : error.Message;
                return Results.Ok(new { agentConfigured = true, agentAvailable = false, agentError = message, engines = Array.Empty<object>(), jobs = Array.Empty<object>() });
            }
        });

        var check = group.MapPost("/nodes/{id:guid}/check-updates", async (Guid id, GatewayDbContext db, IHttpClientFactory factory,
            UpstreamCredentialProtector protector, CancellationToken token) =>
        {
            var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            if (node is null) return Results.NotFound();
            try
            {
                return Results.Ok(await NodeModelManagementEndpoints.SendAgentAsync<IReadOnlyList<AgentEngineCheck>>(
                    factory.CreateClient("node-management"), node, protector, HttpMethod.Post, "/v1/runtime-engines/check-updates", null, token));
            }
            catch (NodeModelManagementEndpoints.AgentException error) { return AgentProblem(error); }
        });

        var start = group.MapPost("/nodes/{id:guid}/{engineId}/jobs", async (Guid id, string engineId, EngineJobRequest request,
            GatewayDbContext db, IHttpClientFactory factory, UpstreamCredentialProtector protector, HttpContext http, CancellationToken token) =>
        {
            if (!ValidEngineId(engineId) || request.Action is not ("install" or "update"))
                return Results.BadRequest(new { error = "invalid_engine_request" });
            var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            if (node is null) return Results.NotFound();
            try
            {
                var job = await NodeModelManagementEndpoints.SendAgentAsync<AgentEngineJob>(factory.CreateClient("node-management"), node, protector,
                    HttpMethod.Post, $"/v1/runtime-engines/{Uri.EscapeDataString(engineId)}/jobs", new { action = request.Action }, token);
                NodeModelManagementEndpoints.AddAudit(db, ManagedModelOperations.Caller.From(http), "engine." + request.Action, "node", id.ToString(),
                    new { engine = engineId, node = node.Name });
                await db.SaveChangesAsync(token);
                return Results.Accepted(value: job);
            }
            catch (NodeModelManagementEndpoints.AgentException error) { return AgentProblem(error); }
        });

        var cancel = group.MapDelete("/nodes/{id:guid}/jobs/{jobId:guid}", async (Guid id, Guid jobId, GatewayDbContext db,
            IHttpClientFactory factory, UpstreamCredentialProtector protector, CancellationToken token) =>
        {
            var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            if (node is null) return Results.NotFound();
            try
            {
                await NodeModelManagementEndpoints.SendAgentAsync<JsonElement>(factory.CreateClient("node-management"), node, protector,
                    HttpMethod.Delete, "/v1/runtime-engines/jobs/" + jobId, null, token);
                return Results.Accepted();
            }
            catch (NodeModelManagementEndpoints.AgentException error) { return AgentProblem(error); }
        });

        var remove = group.MapDelete("/nodes/{id:guid}/{engineId}", async (Guid id, string engineId, GatewayDbContext db,
            IHttpClientFactory factory, UpstreamCredentialProtector protector, HttpContext http, CancellationToken token) =>
        {
            if (!ValidEngineId(engineId)) return Results.BadRequest(new { error = "invalid_engine_request" });
            var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
            if (node is null) return Results.NotFound();
            try
            {
                var result = await NodeModelManagementEndpoints.SendAgentAsync<JsonElement>(factory.CreateClient("node-management"), node, protector,
                    HttpMethod.Delete, "/v1/runtime-engines/" + Uri.EscapeDataString(engineId), null, token);
                NodeModelManagementEndpoints.AddAudit(db, ManagedModelOperations.Caller.From(http), "engine.remove", "node", id.ToString(),
                    new { engine = engineId, node = node.Name });
                await db.SaveChangesAsync(token);
                return Results.Ok(result);
            }
            catch (NodeModelManagementEndpoints.AgentException error) { return AgentProblem(error); }
        });

        var policy = group.MapPut("/nodes/{id:guid}/{engineId}/policy", async (Guid id, string engineId, EnginePolicyRequest request,
            GatewayDbContext db, HttpContext http, CancellationToken token) =>
        {
            if (!ValidEngineId(engineId)) return Results.BadRequest(new { error = "invalid_engine_request" });
            if (!RuntimeEngineSchedule.TryParseTime(request.UpdateAtLocal, out _))
                return Results.BadRequest(new { error = "invalid_time", message = "The time must be HH:mm, for example 03:30." });
            if (RuntimeEngineSchedule.ResolveZone(request.TimeZone) is null)
                return Results.BadRequest(new { error = "invalid_time_zone", message = "Use an IANA time zone such as Europe/Rome." });
            if (!await db.Nodes.AnyAsync(item => item.Id == id, token)) return Results.NotFound();

            var row = await db.RuntimeEnginePolicies.SingleOrDefaultAsync(item => item.NodeId == id && item.EngineId == engineId, token);
            if (row is null) db.RuntimeEnginePolicies.Add(row = new RuntimeEnginePolicyRecord { NodeId = id, EngineId = engineId });
            var scheduleChanged = row.AutoUpdate != request.AutoUpdate || row.UpdateAtLocal != request.UpdateAtLocal || row.TimeZone != request.TimeZone;
            row.AutoUpdate = request.AutoUpdate;
            row.UpdateAtLocal = request.UpdateAtLocal;
            row.TimeZone = request.TimeZone;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            // Choosing a new time today must not be answered by a run that already happened under the old schedule.
            if (scheduleChanged) { row.LastRunAtUtc = null; row.LastAttemptAtUtc = null; }
            NodeModelManagementEndpoints.AddAudit(db, ManagedModelOperations.Caller.From(http), "engine.policy", "node", id.ToString(),
                new { engine = engineId, request.AutoUpdate, request.UpdateAtLocal, request.TimeZone });
            await db.SaveChangesAsync(token);
            return Results.Ok(PolicyView(row));
        });

        if (entraEnabled)
        {
            check.RequireAuthorization("AdminWrite");
            start.RequireAuthorization("AdminWrite");
            cancel.RequireAuthorization("AdminWrite");
            remove.RequireAuthorization("AdminWrite");
            policy.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    public static bool ValidEngineId(string? id) =>
        id is { Length: > 0 and <= 40 } && id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '-');

    internal static object PolicyView(RuntimeEnginePolicyRecord? policy) => new
    {
        autoUpdate = policy?.AutoUpdate ?? false,
        updateAtLocal = policy?.UpdateAtLocal ?? "03:00",
        timeZone = policy?.TimeZone ?? "UTC",
        lastRunAtUtc = policy?.LastRunAtUtc,
        lastResult = policy?.LastResult,
        lastMessage = policy?.LastMessage
    };

    private static IResult AgentProblem(NodeModelManagementEndpoints.AgentException error) => error.StatusCode switch
    {
        StatusCodes.Status409Conflict => Results.Conflict(new { error = "engine_busy", message = Strip(error.Message) }),
        StatusCodes.Status400BadRequest => Results.BadRequest(new { error = "invalid_engine_request", message = Strip(error.Message) }),
        StatusCodes.Status404NotFound => Results.NotFound(new { error = "not_found", message = Strip(error.Message) }),
        _ => Results.Problem(error.Message, statusCode: StatusCodes.Status502BadGateway)
    };

    // "Management agent returned HTTP 409: {"error":"…"}" -> the Agent's own sentence.
    private static string Strip(string message)
    {
        var brace = message.IndexOf('{');
        if (brace < 0) return message;
        try
        {
            using var document = JsonDocument.Parse(message[brace..]);
            if (document.RootElement.TryGetProperty("error", out var text) && text.ValueKind == JsonValueKind.String) return text.GetString()!;
        }
        catch (JsonException) { /* keep the raw message */ }
        return message;
    }
}
