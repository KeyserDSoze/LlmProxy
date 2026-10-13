using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Api.SystemOne;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Models;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

/// <summary>Persisted, audited System One alias preference; no manual runtime URL is required.</summary>
public static class SystemOneDefaultAdminEndpoints
{
    public sealed record ChangeDefault(string? Model);

    public static IEndpointRouteBuilder MapSystemOneDefaultAdminEndpoints(
        this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/systemone");
        if (entraEnabled) group.RequireAuthorization("AdminRead");

        group.MapGet("/default", async (
            GatewayDbContext db, IDeploymentCatalog catalog,
            IConfiguration config, CancellationToken token) =>
        {
            var available = await SystemOneDefaultResolver.GetPublishedModelNamesAsync(catalog, token);
            var record = await db.SystemOneDefaults.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == SystemOneDefaultRecord.SingletonId, token);
            return Results.Ok(new
            {
                configuredModel = record?.PublicModelName,
                effectiveModel = SystemOneDefaultResolver.Resolve(
                    available, record?.PublicModelName,
                    config["SystemOne:DefaultModel"]),
                options = available,
                record?.UpdatedAtUtc
            });
        });

        var update = group.MapPut("/default", async (
            ChangeDefault request, GatewayDbContext db, IDeploymentCatalog catalog,
            IConfiguration config, HttpContext http, CancellationToken token) =>
        {
            var proposed = request.Model?.Trim();
            if (proposed is { Length: > 160 } || request.Model is not null && proposed?.Length == 0)
                return Results.BadRequest(new { error = "invalid_systemone_default" });
            var available = await SystemOneDefaultResolver.GetPublishedModelNamesAsync(catalog, token);
            if (proposed is not null &&
                !available.Contains(proposed, StringComparer.Ordinal))
                return Results.Conflict(new {
                    error = "systemone_default_not_available",
                    message = "The selected System One model must be enabled and published before it can become the default."
                });

            // Database upsert; singleton primary key prevents divergent defaults
            // across Gateway replicas. Concurrent first writers receive a conflict.
            var setting = await db.SystemOneDefaults
                .FirstOrDefaultAsync(x => x.Id == SystemOneDefaultRecord.SingletonId, token);
            if (setting is null)
            {
                setting = new SystemOneDefaultRecord();
                db.SystemOneDefaults.Add(setting);
            }
            setting.PublicModelName = proposed;
            setting.UpdatedAtUtc = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                    http.User.Identity?.Name ?? "admin",
                "systemone.default.update", "systemone", "default",
                http.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { setting.PublicModelName })));
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException) {
                return Results.Conflict(new { error = "systemone_default_concurrent_change",
                    message = "The default changed concurrently; refresh and try again." });
            }
            return Results.Ok(new
            {
                configuredModel = setting.PublicModelName,
                effectiveModel = SystemOneDefaultResolver.Resolve(
                    available, setting.PublicModelName,
                    config["SystemOne:DefaultModel"]),
                options = available,
                setting.UpdatedAtUtc
            });
        });
        if (entraEnabled) update.RequireAuthorization("AdminWrite");
        return endpoints;
    }
}
