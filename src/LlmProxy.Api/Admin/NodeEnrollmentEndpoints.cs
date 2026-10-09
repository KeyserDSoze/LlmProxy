using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Api.AgentConnectivity;
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

        admin.MapGet("/nodes", async (GatewayDbContext db, CancellationToken token) =>
        {
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(await db.NodeEnrollments.AsNoTracking()
                .Where(x => x.NodeId != null)
                .Select(x => new { x.NodeId, x.Mode, x.LastHeartbeatAtUtc, x.AgentVersion })
                .ToListAsync(token));
        });

        if (entraEnabled) invite.RequireAuthorization("AdminWrite");

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
            var bearer = ReadBearer(httpRequest);
            if (bearer is null || !bearer.StartsWith("lpa_", StringComparison.Ordinal) || bearer.Length > 160)
                return Results.Unauthorized();
            var digest = hasher.Hash(bearer);
            var updated = await db.NodeEnrollments.Where(x => x.NodeId == nodeId && x.AgentSecretHash == digest)
                .ExecuteUpdateAsync(x => x
                    .SetProperty(r => r.LastHeartbeatAtUtc, DateTimeOffset.UtcNow)
                    .SetProperty(r => r.HardwareInventoryJson, request.Inventory.GetRawText())
                    .SetProperty(r => r.AgentVersion, request.AgentVersion), token);
            return updated == 1 ? Results.Ok(new { status = "connected" }) : Results.Unauthorized();
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
    public sealed record AgentHeartbeatRequest(JsonElement Inventory, string? AgentVersion);
}
