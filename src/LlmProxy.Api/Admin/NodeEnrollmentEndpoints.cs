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
        // Public distribution: no Admin login and no pairing secret is required to download.
        // The only variable is a VERIFIED published SemVer; never proxy arbitrary URLs.
        // The checksum belongs to the exact public script in THIS gateway image,
        // not to a potentially newer/different GitHub release.
        // Explicit endpoint also covers hosts whose StaticFile MIME registry does
        // not include .sh; no authentication or invitation is required to download.
        app.MapGet("/downloads/agent/connect-node.sh", (IWebHostEnvironment host) =>
        {
            var path = Path.Combine(host.WebRootPath, "downloads", "agent", "connect-node.sh");
            return File.Exists(path)
                ? Results.File(path, "text/plain; charset=utf-8")
                : Results.NotFound();
        }).AllowAnonymous();

        app.MapGet("/downloads/agent/connect-node.sh.sha256", async (
            IWebHostEnvironment host, CancellationToken token) =>
        {
            var path = Path.Combine(host.WebRootPath, "downloads", "agent", "connect-node.sh");
            if (!File.Exists(path)) return Results.NotFound();
            var bytes = await File.ReadAllBytesAsync(path, token);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            return Results.Text(digest + "  connect-node.sh\n", "text/plain");
        }).AllowAnonymous();

        app.MapGet("/downloads/agent/version", async (ReleaseDiscoveryService releases, CancellationToken token) =>
        {
            var versions = await releases.GetDownloadableAgentVersionsAsync(token);
            return versions.Count == 0
                ? Results.Problem("No published Linux Agent release is available.", statusCode: 503)
                : Results.Text(versions[0] + "\n", "text/plain");
        }).AllowAnonymous();

        app.MapGet("/downloads/agent/{version}/{fileName}", async (
            string version, string fileName, ReleaseDiscoveryService releases, CancellationToken token) =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$"))
                return Results.NotFound();
            var officialName = fileName switch
            {
                "linux-x64.tar.gz" => $"llmproxy-node-agent-{version}-linux-x64.tar.gz",
                "linux-x64.tar.gz.sha256" => $"llmproxy-node-agent-{version}-linux-x64.tar.gz.sha256",
                "linux-arm64.tar.gz" => $"llmproxy-node-agent-{version}-linux-arm64.tar.gz",
                "linux-arm64.tar.gz.sha256" => $"llmproxy-node-agent-{version}-linux-arm64.tar.gz.sha256",
                _ => null
            };
            if (officialName is null) return Results.NotFound();
            var releasesList = await releases.GetDownloadableAgentVersionsAsync(token);
            if (!releasesList.Contains(version, StringComparer.Ordinal)) return Results.NotFound();
            var releaseAsset = $"https://github.com/KeyserDSoze/LlmProxy/releases/download/v{version}/{officialName}";
            return Results.Redirect(releaseAsset, permanent: false);
        }).AllowAnonymous();

        var admin = app.MapGroup("/api/admin/node-enrollment");
        if (entraEnabled) admin.RequireAuthorization("AdminRead");

        admin.MapGet("/downloads", async (ReleaseDiscoveryService releases, CancellationToken token) =>
        {
            var versions = await releases.GetDownloadableAgentVersionsAsync(token);
            var version = versions.FirstOrDefault();
            if (version is null) return Results.Problem("No stable Agent release with both architectures is available.", statusCode: 503);
            var root = $"/downloads/agent/{version}/";
            return Results.Ok(new
            {
                version,
                bootstrap = "/downloads/agent/connect-node.sh",
                bootstrapChecksum = "/downloads/agent/connect-node.sh.sha256",
                x64 = root + "linux-x64.tar.gz",
                x64Checksum = root + "linux-x64.tar.gz.sha256",
                arm64 = root + "linux-arm64.tar.gz",
                arm64Checksum = root + "linux-arm64.tar.gz.sha256"
            });
        });

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

        admin.MapGet("/nodes", async (GatewayDbContext db, AgentRelayHub relay, IServiceProvider services, CancellationToken token) =>
        {
            var records = await db.NodeEnrollments.AsNoTracking()
                .Where(x => x.NodeId != null)
                .Select(x => new { x.NodeId, x.Mode, x.LastHeartbeatAtUtc, x.AgentVersion, x.DesiredAgentVersion, x.AgentUpdateStatus, x.RecoverySecretCreatedAtUtc, HasRecoverySecret = x.RecoverySecretHash != null })
                .ToListAsync(token);
            var bridge = services.GetService<RedisAgentRelayBridge>();
            var states = new Dictionary<Guid, bool>();
            foreach (var record in records.Where(x => x.NodeId.HasValue))
            {
                var id = record.NodeId!.Value;
                if (relay.IsConnected(id)) { states[id] = true; continue; }
                try { states[id] = bridge is not null && await bridge.HasOwnerAsync(id, token); }
                catch (Exception) when (!token.IsCancellationRequested) { states[id] = false; }
            }
            return Results.Ok(records.Select(x => new {
                x.NodeId, x.Mode, x.LastHeartbeatAtUtc, x.AgentVersion, x.DesiredAgentVersion, x.AgentUpdateStatus,
                x.RecoverySecretCreatedAtUtc, x.HasRecoverySecret,
                tunnelConnected = x.NodeId.HasValue && states.GetValueOrDefault(x.NodeId.Value)
            }));
        });

        var update = admin.MapPost("/nodes/{nodeId:guid}/update", async (
            Guid nodeId, AgentUpdateRequest request, GatewayDbContext db,
            ReleaseDiscoveryService releases, HttpContext context, CancellationToken token) =>
        {
            var agent = await db.NodeEnrollments.SingleOrDefaultAsync(
                x => x.NodeId == nodeId && x.AgentSecretHash != null, token);
            if (agent is null) return Results.NotFound();
            // A version swap restarts the local Agent. Never interrupt deployed models
            // during a production request: stop managed deployments through Admin first.
            if (await db.Deployments.AnyAsync(d => d.NodeId == nodeId && d.Enabled &&
                d.ManagedInstallationId != null, token))
                return Results.Conflict(new { error = "active_managed_models",
                    message = "Stop all active managed deployments before upgrading the Agent." });
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

        // Per-node recovery is deliberately separate from the 30-minute invitation.
        // Encrypted-at-rest code is revealable only through audited AdminWrite actions.
        var revealRecovery = admin.MapPost("/nodes/{nodeId:guid}/recovery/reveal", async (
            Guid nodeId, GatewayDbContext db, ApiKeyHasher hasher,
            UpstreamCredentialProtector protector, HttpContext context, CancellationToken token) =>
        {
            var record = await db.NodeEnrollments.SingleOrDefaultAsync(
                x => x.NodeId == nodeId && x.AgentSecretHash != null, token);
            if (record is null) return Results.NotFound();
            if (!protector.IsConfigured) return Results.Problem(
                "Credential encryption is not configured.", statusCode: 503);
            if (record.RecoverySecretHash is null || record.RecoverySecretCiphertext is null)
            {
                var secret = ApiKeyHasher.GenerateSecret("lpr_");
                record.RecoverySecretHash = hasher.Hash(secret);
                record.RecoverySecretCiphertext = protector.Protect(secret);
                record.RecoverySecretCreatedAtUtc = DateTimeOffset.UtcNow;
            }
            var actor = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "local-admin";
            db.AuditEvents.Add(new AuditEvent(actor, "agent.recovery.reveal", "node",
                nodeId.ToString(), context.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { generated = record.RecoverySecretCreatedAtUtc })));
            await db.SaveChangesAsync(token);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { nodeId, recoveryToken = protector.Unprotect(record.RecoverySecretCiphertext),
                record.RecoverySecretCreatedAtUtc });
        });

        var rotateRecovery = admin.MapPost("/nodes/{nodeId:guid}/recovery/rotate", async (
            Guid nodeId, GatewayDbContext db, ApiKeyHasher hasher,
            UpstreamCredentialProtector protector, HttpContext context, CancellationToken token) =>
        {
            var record = await db.NodeEnrollments.SingleOrDefaultAsync(
                x => x.NodeId == nodeId && x.AgentSecretHash != null, token);
            if (record is null) return Results.NotFound();
            if (!protector.IsConfigured) return Results.Problem(
                "Credential encryption is not configured.", statusCode: 503);
            var secret = ApiKeyHasher.GenerateSecret("lpr_");
            record.RecoverySecretHash = hasher.Hash(secret);
            record.RecoverySecretCiphertext = protector.Protect(secret);
            record.RecoverySecretCreatedAtUtc = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent(
                context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "local-admin",
                "agent.recovery.rotate", "node", nodeId.ToString(),
                context.Connection.RemoteIpAddress?.ToString(), "{}"));
            await db.SaveChangesAsync(token);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { nodeId, recoveryToken = secret, record.RecoverySecretCreatedAtUtc });
        });

        if (entraEnabled)
        {
            invite.RequireAuthorization("AdminWrite");
            update.RequireAuthorization("AdminWrite");
            revealRecovery.RequireAuthorization("AdminWrite");
            rotateRecovery.RequireAuthorization("AdminWrite");
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

            // The outbound tunnel authenticates with its own hashed node secret and
            // does not store a provider/management bearer. Only direct mode needs
            // configured encryption for its local management credential.
            if (request.Mode == "direct")
            {
                if (!protector.IsConfigured) return Results.Problem(
                    "Management credential encryption is not configured.", statusCode: 503);
                if (!Uri.TryCreate(request.ManagementBaseAddress, UriKind.Absolute, out var managementUri) ||
                    managementUri.Scheme != Uri.UriSchemeHttp || managementUri.Port != 9900 ||
                    string.IsNullOrWhiteSpace(request.AgentBearer) || request.AgentBearer.Length > 2048)
                    return Results.BadRequest(new { error = "invalid_direct_management_address_or_bearer" });
            }

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
            if (protector.IsConfigured)
            {
                var recoverySecret = ApiKeyHasher.GenerateSecret("lpr_");
                invitation.RecoverySecretHash = hasher.Hash(recoverySecret);
                invitation.RecoverySecretCiphertext = protector.Protect(recoverySecret);
                invitation.RecoverySecretCreatedAtUtc = now;
            }
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

        // Restore a previously registered node without creating a duplicate physical node.
        // Recovery rotates the active Agent secret (revoking lost local identity) but
        // deliberately does not reset deployments, routing, model caches or node ID.
        agents.MapPost("/recover", async (AgentRecoveryRequest request,
            GatewayDbContext db, ApiKeyHasher hasher,
            UpstreamCredentialProtector protector, CancellationToken token) =>
        {
            if (request.NodeId == Guid.Empty || string.IsNullOrWhiteSpace(request.RecoveryToken) ||
                request.RecoveryToken.Length > 200 || !request.RecoveryToken.StartsWith("lpr_", StringComparison.Ordinal) ||
                request.Inventory.ValueKind != JsonValueKind.Object ||
                request.Mode is not ("direct" or "outbound"))
                return Results.BadRequest(new { error = "invalid_recovery_request" });
            var digest = hasher.Hash(request.RecoveryToken);
            var record = await db.NodeEnrollments.SingleOrDefaultAsync(
                x => x.NodeId == request.NodeId && x.RecoverySecretHash == digest &&
                    x.AgentSecretHash != null && x.Mode == request.Mode, token);
            if (record is null) return Results.Unauthorized();
            if (request.AgentVersion?.Length > 80) return Results.BadRequest();
            if (request.Mode == "direct")
            {
                if (!protector.IsConfigured) return Results.Problem(
                    "Credential encryption is not configured.", statusCode: 503);
                if (!Uri.TryCreate(request.ManagementBaseAddress, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttp || uri.Port != 9900 ||
                    string.IsNullOrWhiteSpace(request.AgentBearer) || request.AgentBearer.Length > 2048)
                    return Results.BadRequest(new { error = "invalid_direct_management_address_or_bearer" });
            }
            var node = await db.Nodes.SingleOrDefaultAsync(x => x.Id == request.NodeId, token);
            if (node is null) return Results.NotFound();
            var agentSecret = ApiKeyHasher.GenerateSecret("lpa_");
            record.AgentSecretHash = hasher.Hash(agentSecret);
            record.LastHeartbeatAtUtc = DateTimeOffset.UtcNow;
            record.AgentVersion = request.AgentVersion;
            record.HardwareInventoryJson = request.Inventory.GetRawText();
            record.DesiredAgentVersion = null;
            record.AgentUpdateStatus = null;
            if (request.Mode == "direct")
            {
                node.SetManagementBaseAddress(request.ManagementBaseAddress);
                node.SetManagementBearerTokenCiphertext(protector.Protect(request.AgentBearer!));
            }
            db.AuditEvents.Add(new AuditEvent("node-agent", "agent.recovery.accept", "node",
                request.NodeId.ToString(), null, JsonSerializer.Serialize(new { request.Mode })));
            await db.SaveChangesAsync(token);
            return Results.Ok(new { nodeId = request.NodeId, agentSecret });
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
            // A failed upgrade requires a new explicit Admin action; never keep
            // restarting a working Linux node in an automatic retry loop.
            if (record.DesiredAgentVersion == request.AgentVersion ||
                record.DesiredAgentVersion is string desired &&
                (request.AgentUpdateStatus == "failed:" + desired ||
                    request.AgentUpdateStatus?.StartsWith("failed:" + desired + ":", StringComparison.Ordinal) == true))
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
    public sealed record AgentRecoveryRequest(
        Guid NodeId, string RecoveryToken, string Mode, JsonElement Inventory,
        string? ManagementBaseAddress, string? AgentBearer, string? AgentVersion);
    public sealed record AgentHeartbeatRequest(JsonElement Inventory, string? AgentVersion, string? AgentUpdateStatus = null);
    public sealed record AgentUpdateRequest(string? Version = null);
}
