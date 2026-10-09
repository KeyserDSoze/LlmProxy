using System.Security.Claims;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class InferenceBenchmarkAdminEndpoints
{
    public static IEndpointRouteBuilder MapInferenceBenchmarkAdminEndpoints(this IEndpointRouteBuilder app, bool entraEnabled)
    {
        var group = app.MapGroup("/api/admin/benchmarks");
        if (entraEnabled) group.RequireAuthorization("AdminRead");
        group.MapGet("/deployments/{deploymentId:guid}", async (Guid deploymentId,
            GatewayDbContext db, CancellationToken token) =>
            Results.Ok(await db.BenchmarkJobs.AsNoTracking().Where(j => j.DeploymentId == deploymentId)
                .OrderByDescending(j => j.RequestedAtUtc).Take(10).ToListAsync(token)));
        var start = group.MapPost("/deployments/{deploymentId:guid}", async (
            Guid deploymentId, BenchmarkStartRequest request, GatewayDbContext db,
            HttpContext http, CancellationToken token) =>
        {
            if (!double.IsFinite(request.MaxP95TtftMilliseconds) ||
                request.MaxP95TtftMilliseconds is < 100 or > 300000 ||
                !double.IsFinite(request.MinSuccessRatePercent) ||
                request.MinSuccessRatePercent is < 1 or > 100)
                return Results.BadRequest(new { error = "invalid_benchmark_slo" });
            var deployment = await db.Deployments.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == deploymentId, token);
            if (deployment is null) return Results.NotFound();
            if (!deployment.Enabled || deployment.ManagedInstallationId is null ||
                string.IsNullOrWhiteSpace(deployment.RuntimeBaseAddress))
                return Results.Conflict(new { error = "managed_runtime_must_be_running" });
            if (await db.BenchmarkJobs.AnyAsync(x => x.DeploymentId == deploymentId &&
                (x.Status == "pending" || x.Status == "running"), token))
                return Results.Conflict(new { error = "benchmark_already_running" });
            var job = new InferenceBenchmarkJob
            {
                DeploymentId = deploymentId,
                MaxP95TtftMilliseconds = request.MaxP95TtftMilliseconds,
                MinSuccessRatePercent = request.MinSuccessRatePercent
            };
            db.BenchmarkJobs.Add(job);
            db.AuditEvents.Add(new AuditEvent(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "local-admin",
                "deployment.benchmark.start", "deployment", deploymentId.ToString(),
                http.Connection.RemoteIpAddress?.ToString(), "{\"syntheticPrompt\":true}"));
            try { await db.SaveChangesAsync(token); }
            catch (DbUpdateException) { return Results.Conflict(new { error = "benchmark_already_running" }); }
            return Results.Accepted($"/api/admin/benchmarks/deployments/{deploymentId}", job);
        });
        var cancel = group.MapDelete("/jobs/{jobId:guid}", async (
            Guid jobId, GatewayDbContext db, HttpContext http, CancellationToken token) =>
        {
            var cancelled = await db.BenchmarkJobs
                .Where(j => j.Id == jobId && (j.Status == "pending" || j.Status == "running"))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "cancelled")
                    .SetProperty(j => j.CompletedAtUtc, DateTimeOffset.UtcNow), token);
            if (cancelled == 0) return Results.Conflict(new { error = "benchmark_not_active" });
            db.AuditEvents.Add(new AuditEvent(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "local-admin",
                "deployment.benchmark.cancel", "benchmark", jobId.ToString(),
                http.Connection.RemoteIpAddress?.ToString(), null));
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        });
        if (entraEnabled)
        {
            start.RequireAuthorization("AdminWrite");
            cancel.RequireAuthorization("AdminWrite");
        }
        return app;
    }

    public sealed record BenchmarkStartRequest(double MaxP95TtftMilliseconds = 5000,
        double MinSuccessRatePercent = 99);
}
