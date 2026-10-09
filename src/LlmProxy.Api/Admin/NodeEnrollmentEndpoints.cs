using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Api.AgentConnectivity;
using LlmProxy.Api.Product;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class NodeEnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapNodeEnrollmentEndpoints(this IEndpointRouteBuilder app, bool entraEnabled)
    {
        var admin = app.MapGroup("/api/admin/node-enrollment");
        if (entraEnabled) admin.RequireAuthorization("AdminRead");

        var invite = admin.MapPost("/invitations", async (
            GatewayDbContext db, ApiKeyHasher hasher, HttpContext context,
            CancellationToken token) =>
        {
            var secret = ApiKeyHasher.GenerateSecret("lpe_");
            var record = new NodeEnrollmentRecord
            {
                InvitationHash = hasher.Hash(secret),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30)
            };
            db.NodeEnrollments.Add(record);
            db.AuditEvents.Add(new AuditEvent(
                context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "local-admin",
                "node.enrollment.invite", "node_enrollment", record.Id.ToString(),
                context.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { record.ExpiresAtUtc })));
            await db.SaveChangesAsync(token);
            return Results.Ok(new { enrollmentToken = secret, record.ExpiresAtUtc });
        });

        admin.MapGet("/nodes", async (GatewayDbContext db, AgentRelayHub relay, CancellationToken token) =>
        {
            var records = await db.NodeEnrollments.AsNoTracking()
                .Where(x => x.NodeId != null)
                .Select(x => new { x.NodeId, x.Mode, x.LastHeartbeatAtUtc, x.AgentVersion, x.DesiredAgentVersion, x.AgentUpdateStatus })
                .ToListAsync(token);
            return Results.Ok(records.Select(x => new {
                x.NodeId, x.Mode, x.LastHeartbeatAtUtc, x.AgentVersion, x.DesiredAgentVersion, x.AgentUpdateStatus,
                tunnelConnected = x.NodeId.HasValue && relay.IsConnected(x.NodeId.Value)
            }));
        });

        var update = admin.MapPost("/nodes/{nodeId:guid}/update", async (
            Guid nodeId, AgentUpdateRequest request, GatewayDbContext db,
            ReleaseDiscoveryService releases, HttpContext context, CancellationToken token) =>
        {
            var agent = await db.NodeEnrollments.SingleOrDefaultAsync(
                x => x.NodeId == nodeId && x.AgentSecretHash != null, token);
            if (agent is null) return Results.NotFound();
            var available = await releases.GetAvailableAsync(agent.AgentVersion ?? "0.0.0", token);
            var selected = string.IsNullOrWhiteSpace(request.Version)
                ? available.FirstOrDefault(x => x.IsNewer)
                : available.FirstOrDefault(x => x.Version == request.Version && x.IsNewer);
            if (selected is null)
                return Results.BadRequest(new { error = "no_verified_newer_release" });
            agent.DesiredAgentVersion = selected.Version;
            db.AuditEvents.Add(new AuditEvent(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "local-admin",
                "agent.update.schedule", "node", nodeId.ToString(),
                context.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { version = selected.Version })));
            await db.SaveChangesAsync(token);
            return Results.Accepted($"/api/admin/node-enrollment/nodes", new { nodeId, desiredAgentVersion = selected.Version });
        });

        if (entraEnabled)
        {
            invite.RequireAuthorization("AdminWrite");
            update.RequireAuthorization("AdminWrite");
        }

        // The agent endpoints use separate random per-agent credentials and never accept browser cookies as auth.
        var agents = app.MapGroup("/api/agent-connection");
        agents.MapPost("/enroll", async (AgentEnrollmentRequest request,
            GatewayDbContext db, ApiKeyHasher hasher,
            UpstreamCredentialProtector protector, CancellationToken token) =>
        {
            if (request.Token is null || request.Token.Length > 200 ||
                request.Inventory.ValueKind != JsonValueKind.Object ||
                request.Mode is not ("direct" or "outbound"))
                return Results.BadRequest(new { error = "invalid_registration" });

            var inviteHash = hasher.Hash(request.Token);
            var now = DateTimeOffset.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var consumed = await db.NodeEnrollments
                .Where(x => x.InvitationHash == inviteHash &&
                    x.ConsumedAtUtc == null && x.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ConsumedAtUtc, now), token);
            if (consumed != 1)
                return Results.Unauthorized();

            if (!protector.IsConfigured) return Results.Problem(
                "Upstream credential encryption is not configured.", statusCode: 503);

            var invitation = await db.NodeEnrollments.SingleAsync(x => x.InvitationHash == inviteHash, token);
            var machine = string.IsNullOrWhiteSpace(request.Hostname) ? "unidentified-agent" : request.Hostname.Trim();
            machine = new string(machine.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').Take(80).ToArray());
            if (machine.Length == 0) machine = "agent";
            var name = machine + "-" + invitation.Id.ToString("N")[..8];
            // No deployment can route until an actual runtime has started and passed health checks.
            var node = new InferenceNode(name, "http://127.0.0.1:1");
            if (request.Mode == "outbound")
            {
                node.SetManagementBaseAddress(AgentRelayHub.Root(node.Id) + "/management");
            }
            if (request.Mode == "direct" && Uri.TryCreate(request.ManagementBaseAddress, UriKind.Absolute, out var baseUri) &&
                baseUri.Scheme == Uri.UriSchemeHttp && baseUri.Port is > 0 and <= 65535 &&
                !string.IsNullOrWhiteSpace(request.AgentBearer))
            {
                node.SetManagementBaseAddress(request.ManagementBaseAddress);
                node.SetManagementBearerTokenCiphertext(protector.Protect(request.AgentBearer));
            }

            var agentSecret = ApiKeyHasher.GenerateSecret("lpa_");
            invitation.NodeId = node.Id;
            invitation.AgentSecretHash = hasher.Hash(agentSecret);
            invitation.LastHeartbeatAtUtc = now;
            invitation.HardwareInventoryJson = request.Inventory.GetRawText();
            invitation.Mode = request.Mode;
            invitation.AgentVersion = request.AgentVersion?[..Math.Min(request.AgentVersion.Length, 80)];
            db.Nodes.Add(node);
            db.AuditEvents.Add(new AuditEvent("node-agent", "node.enrollment.accept", "node",
                node.Id.ToString(), null, JsonSerializer.Serialize(new { name, request.Mode })));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Results.Ok(new { nodeId = node.Id, agentSecret });
        });

        agents.MapPost("/{nodeId:guid}/heartbeat", async (Guid nodeId,
            AgentHeartbeatRequest request, GatewayDbContext db, ApiKeyHasher hasher,
            HttpRequest httpRequest, CancellationToken token) =>
        {
            if (request.Inventory.ValueKind != JsonValueKind.Object ||
                request.AgentVersion?.Length > 80 || request.AgentUpdateStatus?.Length > 80)
                return Results.BadRequest();
            var bearer = ReadBearer(httpRequest);
            if (bearer is null || !bearer.StartsWith("lpa_", StringComparison.Ordinal) || bearer.Length > 160)
                return Results.Unauthorized();
            var digest = hasher.Hash(bearer);
            var record = await db.NodeEnrollments.SingleOrDefaultAsync(
                x => x.NodeId == nodeId && x.AgentSecretHash == digest, token);
            if (record is null) return Results.Unauthorized();
            record.LastHeartbeatAtUtc = DateTimeOffset.UtcNow;
            record.HardwareInventoryJson = request.Inventory.GetRawText();
            record.AgentVersion = request.AgentVersion;
            record.AgentUpdateStatus = request.AgentUpdateStatus;
            if (record.DesiredAgentVersion == request.AgentVersion)
                record.DesiredAgentVersion = null;
            await db.SaveChangesAsync(token);
            return Results.Ok(new { status = "connected", desiredAgentVersion = record.DesiredAgentVersion });
        });
        agents.MapGet("/{nodeId:guid}/tunnel", async (Guid nodeId,
            GatewayDbContext db, ApiKeyHasher hasher, AgentRelayHub relay, HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { error = "websocket_required" });
            var bearer = ReadBearer(context.Request);
            if (bearer is null || !bearer.StartsWith("lpa_", StringComparison.Ordinal) || bearer.Length > 160)
                return Results.Unauthorized();
            var hash = hasher.Hash(bearer);
            var valid = await db.NodeEnrollments.AsNoTracking().AnyAsync(
                x => x.NodeId == nodeId && x.AgentSecretHash == hash && x.Mode == "outbound",
                context.RequestAborted);
            if (!valid) return Results.Unauthorized();
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await relay.AttachAsync(nodeId, socket, context.RequestAborted);
            return Results.Empty;
        });
        return app;
    }

    private static string? ReadBearer(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim() : null;
    }

    public sealed record AgentEnrollmentRequest(
        string Token, string Hostname, string Mode, JsonElement Inventory,
        string? ManagementBaseAddress, string? AgentBearer, string? AgentVersion);
    public sealed record AgentHeartbeatRequest(JsonElement Inventory, string? AgentVersion, string? AgentUpdateStatus = null);
    public sealed record AgentUpdateRequest(string? Version = null);
}
