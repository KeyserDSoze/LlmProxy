using System.Text.Json;

namespace LlmProxy.NodeAgent;

public sealed record ModelStartJob(
    Guid Id, string InstallationId, string Status, string Detail,
    string? Error, ManagedModelState? Result,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc = null, bool Restart = false);

public sealed record ModelStartJobRequest(Guid JobId);

/// <summary>
/// Persistent start operations survive browser/relay disconnections.
/// Desired-running state survives Agent restarts; the guarded bootstrap worker owns recovery.
/// </summary>
public sealed class ModelStartJobManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _file;
    private readonly Dictionary<Guid, ModelStartJob> _jobs;
    private readonly Dictionary<Guid, CancellationTokenSource> _active = [];
    private readonly DockerModelRuntimeManager _runtime;
    private readonly ManagedModelRegistry _registry;
    private readonly IHostApplicationLifetime _host;

    public ModelStartJobManager(NodeAgentOptions options, DockerModelRuntimeManager runtime,
        ManagedModelRegistry registry, IHostApplicationLifetime host, ILogger<ModelStartJobManager>? logger = null)
    {
        _runtime = runtime;
        _registry = registry;
        _host = host;
        Directory.CreateDirectory(options.DataDirectory);
        _file = Path.Combine(options.DataDirectory, "start-jobs.json");
        var saved = AgentStateFile.ReadList<ModelStartJob>(_file, Json, logger);
        _jobs = saved.ToDictionary(j => j.Id, j => j.Status is "queued" or "starting"
            ? j with { Status = "interrupted", Detail = "Agent restarted during startup.",
                Error = "Startup interrupted; guarded model bootstrap will retry if the model is still wanted.",
                CompletedAtUtc = DateTimeOffset.UtcNow }
            : j);
        Persist();
    }

    public IReadOnlyList<ModelStartJob> List()
    {
        lock (_gate) return _jobs.Values.OrderByDescending(x => x.CreatedAtUtc).Take(50).ToArray();
    }

    public ModelStartJob Enqueue(string installationId, bool restart = false, Guid? jobId = null)
    {
        if (string.IsNullOrWhiteSpace(installationId) || installationId.Length > 100 ||
            !System.Text.RegularExpressions.Regex.IsMatch(installationId, "^[a-z0-9-]+$"))
            throw new ArgumentException("Invalid model installation ID.", nameof(installationId));
        lock (_gate)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("Start job id must not be empty.", nameof(jobId));
            if (jobId is { } requestedId && _jobs.TryGetValue(requestedId, out var existing))
            {
                if (existing.InstallationId != installationId || existing.Restart != restart)
                    throw new InvalidOperationException("Start job id is already used by another operation.");
                return existing;
            }
            if (_jobs.Values.Any(x => x.Status is "queued" or "starting"))
                throw new InvalidOperationException("A runtime start is already in progress. Inspect its job before retrying.");
            foreach (var old in _jobs.Values.Where(x => x.Status is not ("queued" or "starting"))
                .OrderBy(x => x.CreatedAtUtc).Take(Math.Max(0, _jobs.Count - 99)).ToArray())
                _jobs.Remove(old.Id);
            var job = new ModelStartJob(jobId ?? Guid.NewGuid(), installationId, "queued",
                "Queued on Agent; browser and tunnel disconnects cannot cancel this job.",
                null, null, DateTimeOffset.UtcNow, Restart: restart);
            _jobs.Add(job.Id, job);
            Persist();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_host.ApplicationStopping);
            _active.Add(job.Id, cts);
            _ = Task.Run(() => ExecuteAsync(job.Id, cts));
            return job;
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var cancel)) return false;
            cancel.Cancel();
            return true;
        }
    }

    public void CancelInstallation(string installationId)
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Values.Where(job => job.InstallationId == installationId))
                if (_active.TryGetValue(job.Id, out var cancellation)) cancellation.Cancel();
        }
    }

    private async Task ExecuteAsync(Guid id, CancellationTokenSource cts)
    {
        try
        {
            var job = Get(id)!;
            // Persist intent before stopping: an Agent reboot between stop and start must still recover this model.
            var record = await _registry.FindAsync(job.InstallationId, cts.Token)
                ?? throw new KeyNotFoundException("Installation no longer exists.");
            await _registry.UpsertAsync(record with { DesiredRunning = true }, cts.Token);
            Update(id, x => x with { Status = "starting", Detail =
                "Starting Docker runtime; waiting for model /health." });
            if (job.Restart) await _runtime.StopAsync(job.InstallationId, cts.Token, preserveDesiredRunning: true);
            var lastDetail = DateTimeOffset.MinValue;
            var result = await _runtime.StartAsync(job.InstallationId, cts.Token, detail =>
            {
                // Long GGUF preparation reports often; persist at most once per second.
                if (DateTimeOffset.UtcNow - lastDetail < TimeSpan.FromSeconds(1)) return;
                lastDetail = DateTimeOffset.UtcNow;
                Update(id, x => x with { Detail = detail.Length > 250 ? detail[..250] : detail });
            });
            Update(id, x => x with { Status = "completed", Detail =
                "Runtime healthy. Routing activation requires a separate verified operation.",
                Result = result, CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException)
        {
            if (!_host.ApplicationStopping.IsCancellationRequested)
            {
                var record = await _registry.FindAsync(Get(id)!.InstallationId, CancellationToken.None);
                if (record is not null) await _registry.UpsertAsync(record with { DesiredRunning = false }, CancellationToken.None);
            }
            Update(id, x => x with { Status = _host.ApplicationStopping.IsCancellationRequested ? "interrupted" : "cancelled",
                Detail = "Startup cancelled by operator or Agent shutdown; cleanup requested.",
                Error = "Startup cancelled before readiness.", CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception exception)
        {
            var error = exception.Message;
            try
            {
                var state = await _registry.FindAsync(Get(id)!.InstallationId, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(state?.Error)) error = state.Error;
            }
            catch (Exception) { /* Keep the primary error. */ }
            Update(id, x => x with { Status = "failed", Detail =
                "Runtime start failed. Last Docker startup output is included when available.",
                Error = error.Length > 3000 ? error[..3000] : error,
                CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        finally
        {
            lock (_gate) _active.Remove(id);
            cts.Dispose();
        }
    }

    private ModelStartJob? Get(Guid id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    private void Update(Guid id, Func<ModelStartJob, ModelStartJob> transform)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var previous)) return;
            _jobs[id] = transform(previous);
            Persist();
        }
    }

    private void Persist()
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            _jobs.Values.OrderByDescending(x => x.CreatedAtUtc).Take(100), Json));
        File.Move(temp, _file, true);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
