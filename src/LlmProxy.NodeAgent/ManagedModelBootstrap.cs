namespace LlmProxy.NodeAgent;

/// <summary>Where the automatic restart of one model stands.</summary>
public sealed class BootstrapAttempt
{
    public int Attempts { get; set; }
    public Guid? JobId { get; set; }
    public DateTimeOffset NextAtUtc { get; set; }
    public bool GaveUp { get; set; }
    public string? Reason { get; set; }
    /// <summary>When the last automatic start succeeded. A model that dies again soon after keeps its count, so a crash loop ends.</summary>
    public DateTimeOffset? LastCompletedAtUtc { get; set; }
}

/// <summary>
/// The decisions of the automatic restart, kept pure so they can be tested: which models are wanted, how long to wait after a
/// failure, and when to stop trying.
/// </summary>
public static class ManagedModelBootstrap
{
    /// <summary>After this many failed starts in a row the Agent stops and leaves the model for an operator.</summary>
    public const int MaxAttempts = 5;

    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)];

    /// <summary>How long a hot GPU, a missing driver or a Docker daemon that is not up yet is waited out without counting as a failure.</summary>
    public static readonly TimeSpan EnvironmentRetry = TimeSpan.FromSeconds(30);

    /// <summary>A model has to stay up this long after an automatic start before its failure count is forgotten.</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(10);

    /// <summary>The operator's last word: running after a verified start, stopped after a stop. Older records count as wanted if they were running.</summary>
    public static bool IsWanted(ManagedModelRecord record) => record.DesiredRunning ?? record.Status == "running";

    public static TimeSpan DelayAfterFailure(int failedAttempts) => Backoff[Math.Clamp(failedAttempts - 1, 0, Backoff.Length - 1)];

    /// <summary>Wanted, but not running and not being installed: what the Agent should start again.</summary>
    public static bool NeedsStart(ManagedModelRecord record, string liveStatus) =>
        IsWanted(record) && liveStatus is not ("running" or "installing");

    /// <summary>The Agent's own summary of an automatic restart, shown on the model card.</summary>
    public static (string Status, string Detail)? Describe(BootstrapAttempt? attempt, DateTimeOffset now)
    {
        if (attempt is null) return null;
        if (attempt.GaveUp)
            return ("gave_up", $"Stopped trying after {attempt.Attempts} failed starts. Start it by hand once the cause is fixed." +
                (attempt.Reason is { Length: > 0 } reason ? " Last error: " + reason : ""));
        if (attempt.JobId is not null) return ("starting", $"Starting automatically (attempt {attempt.Attempts} of {MaxAttempts}).");
        if (attempt.Attempts > 0)
            return ("retrying", $"Start failed ({attempt.Attempts} of {MaxAttempts}); next try in {Math.Max(0, (int)Math.Ceiling((attempt.NextAtUtc - now).TotalMinutes))} min." +
                (attempt.Reason is { Length: > 0 } why ? " " + why : ""));
        return ("waiting", attempt.Reason ?? "Waiting for the server to be ready.");
    }
}

/// <summary>Shared view of the restart attempts, so the model list can say what the Agent is doing.</summary>
public sealed class BootstrapTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BootstrapAttempt> _attempts = new(StringComparer.Ordinal);

    public BootstrapAttempt Get(string installationId)
    {
        lock (_gate) return _attempts.TryGetValue(installationId, out var found) ? found : _attempts[installationId] = new BootstrapAttempt();
    }

    public void Forget(string installationId) { lock (_gate) _attempts.Remove(installationId); }

    public IReadOnlyList<(string Id, BootstrapAttempt Attempt)> Snapshot()
    {
        lock (_gate) return _attempts.Select(entry => (entry.Key, entry.Value)).ToArray();
    }

    public ManagedModelsResponse Annotate(ManagedModelsResponse response)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
            return new ManagedModelsResponse(response.Models.Select(model =>
            {
                if (!_attempts.TryGetValue(model.InstallationId, out var attempt) || model.Status == "running") return model;
                return ManagedModelBootstrap.Describe(attempt, now) is { } text ? model with { AutoStart = text.Status, AutoStartDetail = text.Detail } : model;
            }).ToArray());
    }
}

/// <summary>
/// Brings back the models that should be running. After a reboot, a power cut or a crash, Docker does not restart GPU
/// containers by itself (on purpose: a restart loop on a hot or failing GPU is harmful), so the Agent does it with care:
/// one model at a time, never while the GPU is above its start temperature, only once Docker and the GPU driver are up,
/// with growing pauses between failures and a limit after which it stops and says so. Engines are made sure of first.
/// </summary>
public sealed class ManagedModelBootstrapWorker(
    NodeAgentOptions options, ManagedModelRegistry registry, DockerModelRuntimeManager manager, ModelStartJobManager startJobs,
    RuntimeEngineService engines, RuntimeEngineJobManager engineJobs, GpuRuntimeSafety gpuSafety, BootstrapTracker tracker,
    ProcessRunner runner, ILogger<ManagedModelBootstrapWorker> logger) : BackgroundService
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.BootstrapModels) { logger.LogInformation("Automatic restart of models is switched off (NodeAgent:BootstrapModels)."); return; }
        try { await Task.Delay(TimeSpan.FromSeconds(options.BootstrapDelaySeconds), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Automatic model restart pass failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PassAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await registry.ReadAsync(token);
        var live = (await manager.ListAsync(token)).Models.ToDictionary(model => model.InstallationId, model => model.Status, StringComparer.Ordinal);
        var wanted = records.Where(record => ManagedModelBootstrap.NeedsStart(record, live.GetValueOrDefault(record.InstallationId, "stopped")))
            .OrderBy(record => record.Port).ToList();
        var jobs = startJobs.List();
        var known = records.ToDictionary(record => record.InstallationId, StringComparer.Ordinal);

        // Settle what the attempts already in flight came to, and forget the models that are stable again.
        foreach (var (id, attempt) in tracker.Snapshot())
        {
            if (!known.TryGetValue(id, out var record)) { tracker.Forget(id); continue; }
            if (attempt.JobId is { } jobId && jobs.FirstOrDefault(job => job.Id == jobId) is { } finished && finished.Status is not ("queued" or "starting"))
            {
                attempt.JobId = null;
                if (finished.Status == "completed") { attempt.LastCompletedAtUtc = now; attempt.Reason = null; attempt.NextAtUtc = now + TimeSpan.FromMinutes(1); continue; }
                attempt.Reason = finished.Error?.Split('\n')[0];
                if (attempt.Attempts >= ManagedModelBootstrap.MaxAttempts)
                {
                    attempt.GaveUp = true;
                    logger.LogWarning("Gave up restarting {Model} after {Attempts} failed starts.", id, attempt.Attempts);
                }
                else attempt.NextAtUtc = now + ManagedModelBootstrap.DelayAfterFailure(attempt.Attempts);
            }
            else if (attempt.JobId is null && !ManagedModelBootstrap.NeedsStart(record, live.GetValueOrDefault(id, "stopped")) &&
                     (attempt.LastCompletedAtUtc is not { } done || now - done > ManagedModelBootstrap.StableAfter))
                tracker.Forget(id);
        }
        if (wanted.Count == 0) return;

        // One start at a time: the Agent never runs two, and a second model would compete for the GPU memory the first one needs.
        if (jobs.Any(job => job.Status is "queued" or "starting")) return;

        foreach (var record in wanted)
        {
            var attempt = tracker.Get(record.InstallationId);
            if (attempt.GaveUp || attempt.JobId is not null || now < attempt.NextAtUtc) continue;

            if (await EnvironmentNotReadyAsync(record, attempt, token))
            {
                attempt.NextAtUtc = now + ManagedModelBootstrap.EnvironmentRetry;
                continue;
            }
            if (await EnsureEngineAsync(record, attempt, token))
            {
                attempt.NextAtUtc = now + ManagedModelBootstrap.EnvironmentRetry;
                continue;
            }

            try
            {
                var job = startJobs.Enqueue(record.InstallationId);
                attempt.Attempts++;
                attempt.JobId = job.Id;
                attempt.Reason = null;
                logger.LogInformation("Starting {Model} automatically (attempt {Attempt}).", record.InstallationId, attempt.Attempts);
            }
            catch (InvalidOperationException) { /* another start began in the meantime */ }
            return;
        }
    }

    /// <summary>True (and the reason recorded) while Docker, the GPU driver or the GPU temperature say wait.</summary>
    private async Task<bool> EnvironmentNotReadyAsync(ManagedModelRecord record, BootstrapAttempt attempt, CancellationToken token)
    {
        var docker = await runner.RunAsync(options.DockerExecutable, ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(10), token);
        if (!docker.Success) { attempt.Reason = "Docker is not ready yet."; return true; }

        if (!ManagedRuntimeRestartGuard.RequiresManualGpuRestart(record.Runtime, options.UseNvidiaGpus)) return false;
        var thermal = await gpuSafety.ReadAsync(token);
        if (thermal.StartupBlocked)
        {
            attempt.Reason = thermal.Explanation + " Waiting for it to cool down.";
            return true;
        }
        // The driver can take a little longer than Docker after a boot; wait a few minutes for it before counting a failure.
        if (!thermal.SensorAvailable && DateTimeOffset.UtcNow - _startedAt < TimeSpan.FromMinutes(5))
        {
            attempt.Reason = "Waiting for the NVIDIA driver after the boot.";
            return true;
        }
        attempt.Reason = null;
        return false;
    }

    /// <summary>Makes sure the engine image is there, pulling or building it as a visible engine job when it is not.</summary>
    private async Task<bool> EnsureEngineAsync(ManagedModelRecord record, BootstrapAttempt attempt, CancellationToken token)
    {
        if (RuntimeEngineCatalog.Find(record.Runtime) is not { } engine) return false;
        if (await engines.IsInstalledAsync(engine.Id, token)) return false;
        if (engineJobs.List().Any(job => job.EngineId == engine.Id && job.Status is "queued" or "running")) { attempt.Reason = $"Installing the {engine.Label} engine first."; return true; }
        try
        {
            engineJobs.Enqueue(engine.Id, "install");
            attempt.Reason = $"Installing the {engine.Label} engine first.";
            logger.LogInformation("Engine {Engine} is missing; installing it before restarting {Model}.", engine.Id, record.InstallationId);
        }
        catch (InvalidOperationException) { attempt.Reason = "Waiting for another engine operation."; }
        return true;
    }
}
