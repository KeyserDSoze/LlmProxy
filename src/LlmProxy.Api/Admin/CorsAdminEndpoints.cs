using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Application.Security;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

/// <summary>Site-wide browser (CORS) origins and the per-key origin lists administrators manage.</summary>
public static class CorsAdminEndpoints
{
    public sealed record UpdateCorsSettingsRequest(IReadOnlyList<string>? AllowedOrigins, int? MaxAgeSeconds = null);
    public sealed record UpdateAllowedOriginsRequest(IReadOnlyList<string>? AllowedOrigins);

    public static IEndpointRouteBuilder MapCorsAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled) group.RequireAuthorization("AdminRead");

        group.MapGet("/cors-settings", async (GatewayDbContext db, CancellationToken token) =>
        {
            var record = await db.CorsSettings.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == CorsSettingsRecord.SingletonId, token);
            return Results.Ok(ToResponse(record));
        });

        var update = group.MapPut("/cors-settings", async (
            UpdateCorsSettingsRequest request, GatewayDbContext db, CorsPolicyState state,
            HttpContext http, CancellationToken token) =>
        {
            IReadOnlyList<string> origins;
            try { origins = OriginPolicy.NormalizeList(request.AllowedOrigins); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = "invalid_origins", message = error.Message }); }
            if (request.MaxAgeSeconds is < 0 or > CorsSettings.MaxAgeLimitSeconds)
                return Results.BadRequest(new { error = "invalid_max_age",
                    message = $"Preflight cache must be between 0 and {CorsSettings.MaxAgeLimitSeconds} seconds." });

            var record = await db.CorsSettings.FirstOrDefaultAsync(item => item.Id == CorsSettingsRecord.SingletonId, token);
            if (record is null)
            {
                record = new CorsSettingsRecord();
                db.CorsSettings.Add(record);
            }

            record.AllowedOriginsText = OriginPolicy.ToStored(origins);
            record.MaxAgeSeconds = CorsSettings.ClampMaxAge(request.MaxAgeSeconds);
            record.UpdatedAtUtc = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent(Actor(http), "cors.settings.update", "cors_settings", "site",
                http.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { origins, record.MaxAgeSeconds })));
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "cors_settings_concurrent_change",
                    message = "The CORS settings changed concurrently; refresh and try again." });
            }

            state.Replace(new CorsSettings(origins, record.MaxAgeSeconds));
            return Results.Ok(ToResponse(record));
        });

        var updateCredential = group.MapPut("/api-credentials/{id:guid}/allowed-origins", async (
            Guid id, UpdateAllowedOriginsRequest request, GatewayDbContext db, HttpContext http, CancellationToken token) =>
        {
            var credential = await db.ApiCredentials.FindAsync([id], token);
            if (credential is null) return Results.NotFound();
            try { credential.SetAllowedOrigins(request.AllowedOrigins); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = "invalid_origins", message = error.Message }); }

            db.AuditEvents.Add(new AuditEvent(Actor(http), "credential.origins.update", "api_credential", credential.Id.ToString(),
                http.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { credential.Name, credential.KeyPrefix, origins = credential.AllowedOrigins })));
            await db.SaveChangesAsync(token);
            return Results.Ok(new { credential.Id, allowedOrigins = credential.AllowedOrigins });
        });

        if (entraEnabled)
        {
            update.RequireAuthorization("AdminWrite");
            updateCredential.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    /// <summary>Reads the persisted singleton into the in-memory state used by the request path.</summary>
    public static async Task LoadAsync(GatewayDbContext db, CorsPolicyState state, CancellationToken token)
    {
        var record = await db.CorsSettings.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == CorsSettingsRecord.SingletonId, token);
        state.Replace(record is null
            ? CorsSettings.Default
            : new CorsSettings(OriginPolicy.ParseStored(record.AllowedOriginsText), CorsSettings.ClampMaxAge(record.MaxAgeSeconds)));
    }

    private static object ToResponse(CorsSettingsRecord? record) => new
    {
        allowedOrigins = OriginPolicy.ParseStored(record?.AllowedOriginsText),
        maxAgeSeconds = record?.MaxAgeSeconds ?? CorsSettings.DefaultMaxAgeSeconds,
        updatedAtUtc = record?.UpdatedAtUtc
    };

    private static string Actor(HttpContext http) =>
        http.User.FindFirstValue("preferred_username") ??
        http.User.FindFirstValue(ClaimTypes.Email) ??
        http.User.Identity?.Name ?? "local-admin";
}

/// <summary>Keeps replicas converging on the persisted CORS settings without touching PostgreSQL per request.</summary>
public sealed class CorsSettingsRefreshWorker(
    IServiceScopeFactory scopes, CorsPolicyState state, ILogger<CorsSettingsRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await CorsAdminEndpoints.LoadAsync(scope.ServiceProvider.GetRequiredService<GatewayDbContext>(), state, stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "CORS settings refresh failed; keeping the previous settings.");
            }
        }
    }
}
