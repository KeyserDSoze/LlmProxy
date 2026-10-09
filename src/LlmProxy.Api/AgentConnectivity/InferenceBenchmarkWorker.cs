using System.Net.Http.Headers;
using System.Text.Json;
using LlmProxy.Benchmarking;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.AgentConnectivity;

/// <summary>Persistent queued synthetic-only test. Atomic DB claim prevents duplicate execution across API replicas.</summary>
public sealed class InferenceBenchmarkWorker(
    IServiceScopeFactory scopes, IHttpClientFactory clients, UpstreamCredentialProtector protector,
    ILogger<InferenceBenchmarkWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DrainOneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning(e, "Benchmark scheduler cycle failed."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }

    private async Task DrainOneAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        // Crash recovery: terminated workers cannot leave deployment-wide permanent locks.
        var expiry = DateTimeOffset.UtcNow.AddMinutes(-35);
        await db.BenchmarkJobs.Where(j => j.Status == "running" && j.StartedAtUtc < expiry)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "failed")
                .SetProperty(j => j.Error, "Benchmark worker timed out; retry from Admin.")
                .SetProperty(j => j.CompletedAtUtc, DateTimeOffset.UtcNow), token);

        var next = await db.BenchmarkJobs.AsNoTracking().Where(j => j.Status == "pending")
            .OrderBy(j => j.RequestedAtUtc).Select(j => j.Id).FirstOrDefaultAsync(token);
        if (next == Guid.Empty) return;
        var claimed = await db.BenchmarkJobs.Where(j => j.Id == next && j.Status == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "running")
                .SetProperty(j => j.StartedAtUtc, DateTimeOffset.UtcNow), token);
        if (claimed != 1) return;
        try
        {
            var job = await db.BenchmarkJobs.AsNoTracking().SingleAsync(j => j.Id == next, token);
            var deployment = await db.Deployments.AsNoTracking()
                .SingleOrDefaultAsync(d => d.Id == job.DeploymentId, token);
            if (deployment is null || !deployment.Enabled || string.IsNullOrWhiteSpace(deployment.RuntimeBaseAddress))
                throw new InvalidOperationException("The managed runtime is no longer enabled or has no target.");
            var node = await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == deployment.NodeId, token);
            var model = await db.Models.AsNoTracking().SingleAsync(m => m.Id == deployment.ModelId, token);
            var options = new BenchmarkOptions
            {
                Target = new Uri(deployment.RuntimeBaseAddress),
                Model = model.ProviderModelName,
                ConcurrencyLevels = [1, 2, 4, 8, 12, 16],
                RequestsPerLevel = 40,
                WarmupRequests = 2,
                RequestTimeout = TimeSpan.FromSeconds(60),
                DelayBetweenLevels = TimeSpan.FromSeconds(2),
                MaxP95TtftMilliseconds = job.MaxP95TtftMilliseconds,
                MinSuccessRatePercent = job.MinSuccessRatePercent
            };
            using var client = clients.CreateClient("vllm");
            var secret = deployment.UpstreamBearerTokenCiphertext ?? node.UpstreamBearerTokenCiphertext;
            if (!string.IsNullOrWhiteSpace(secret))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", protector.Unprotect(secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            // Cancellation is persisted so a browser close does not lose operator intent.
            var monitor = Task.Run(async () =>
            {
                while (!timeout.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
                    using var monitorScope = scopes.CreateScope();
                    var monitorDb = monitorScope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                    var status = await monitorDb.BenchmarkJobs.AsNoTracking()
                        .Where(j => j.Id == next).Select(j => j.Status)
                        .FirstOrDefaultAsync(timeout.Token);
                    if (status != "running") { timeout.Cancel(); break; }
                }
            }, CancellationToken.None);
            BenchmarkReport report;
            try { report = await new BenchmarkRunner(client).RunAsync(options, timeout.Token); }
            finally
            {
                timeout.Cancel();
                try { await monitor; } catch (OperationCanceledException) { }
            }
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var completed = await db.BenchmarkJobs.Where(j => j.Id == next && j.Status == "running")
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "completed")
                    .SetProperty(j => j.ReportJson, reportJson)
                    .SetProperty(j => j.CompletedAtUtc, DateTimeOffset.UtcNow), token);
            // Evidence stays advisory: a human must explicitly apply the capacity profile.
            if (completed == 1 && report.Recommendation?.RecommendedMaxConcurrency is int level &&
                level >= 1 && report.Levels.Any(x => x.Concurrency == level))
            {
                var evidence = report.Levels.Single(x => x.Concurrency == level);
                var current = await db.Deployments.SingleOrDefaultAsync(d => d.Id == job.DeploymentId, token);
                if (current is not null)
                {
                    current.SetCapacityProfile(level, evidence.P95TtftMilliseconds,
                        evidence.P95DurationMilliseconds, evidence.OutputTokensPerSecond,
                        "admin-benchmark:" + job.Id.ToString("N"), report.CompletedAtUtc);
                    await db.SaveChangesAsync(token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            await db.BenchmarkJobs.Where(j => j.Id == next && j.Status == "running")
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "failed")
                    .SetProperty(j => j.Error, "Benchmark timed out or was interrupted.")
                    .SetProperty(j => j.CompletedAtUtc, DateTimeOffset.UtcNow), CancellationToken.None);
        }
        catch (Exception error)
        {
            logger.LogWarning("Benchmark {JobId} failed: {Type}", next, error.GetType().Name);
            await db.BenchmarkJobs.Where(j => j.Id == next && j.Status == "running")
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, "failed")
                    .SetProperty(j => j.Error, error is InvalidOperationException ? error.Message[..Math.Min(error.Message.Length, 900)] : error.GetType().Name)
                    .SetProperty(j => j.CompletedAtUtc, DateTimeOffset.UtcNow), token);
        }
    }
}
