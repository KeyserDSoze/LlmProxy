using System.Text.Json;

namespace LlmProxy.NodeAgent;

public sealed record ModelStartJob(
    Guid Id, string InstallationId, string Status, string Detail,
    string? Error, ManagedModelState? Result,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc = null);

/// <summary>
/// Persistent start operations survive browser/relay disconnections.
/// Interrupted Agent restarts require an explicit new Start, never automatic
/// GPU reinitialization after power or thermal incidents.
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
        ManagedModelRegistry registry, IHostApplicationLifetime host)
    {
        _runtime = runtime;
        _registry = registry;
        _host = host;
        Directory.CreateDirectory(options.DataDirectory);
        _file = Path.Combine(options.DataDirectory, "start-jobs.json");
        var saved = File.Exists(_file)
            ? JsonSerializer.Deserialize<List<ModelStartJob>>(File.ReadAllText(_file), Json) ?? []
            : [];
        _jobs = saved.ToDictionary(j => j.Id, j => j.Status is "queued" or "starting"
            ? j with { Status = "interrupted", Detail = "Agent restarted during startup.",
                Error = "No automatic GPU restart after an Agent reboot; check hardware before restarting.",
                CompletedAtUtc = DateTimeOffset.UtcNow }
            : j);
        Persist();
    }

    public IReadOnlyList<ModelStartJob> List()
    {
        lock (_gate) return _jobs.Values.OrderByDescending(x => x.CreatedAtUtc).Take(50).ToArray();
    }

    public ModelStartJob Enqueue(string installationId)
    {
        if (string.IsNullOrWhiteSpace(installationId) || installationId.Length > 100 ||
            !System.Text.RegularExpressions.Regex.IsMatch(installationId, "^[a-z0-9-]+$"))
            throw new ArgumentException("Invalid model installation ID.", nameof(installationId));
        lock (_gate)
        {
            if (_jobs.Values.Any(x => x.Status is "queued" or "starting"))
                throw new InvalidOperationException("A runtime start is already in progress. Inspect its job before retrying.");
            foreach (var old in _jobs.Values.Where(x => x.Status is not ("queued" or "starting"))
                .OrderBy(x => x.CreatedAtUtc).Take(Math.Max(0, _jobs.Count - 99)).ToArray())
                _jobs.Remove(old.Id);
            var job = new ModelStartJob(Guid.NewGuid(), installationId, "queued",
                "Queued on Agent; browser and tunnel disconnects cannot cancel this job.",
                null, null, DateTimeOffset.UtcNow);
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

    private async Task ExecuteAsync(Guid id, CancellationTokenSource cts)
    {
        try
        {
            var job = Get(id)!;
            Update(id, x => x with { Status = "starting", Detail =
                "Starting Docker runtime; waiting for model /health. No automatic crash restarts." });
            var result = await _runtime.StartAsync(job.InstallationId, cts.Token);
            Update(id, x => x with { Status = "completed", Detail =
                "Runtime healthy. Routing activation requires a separate verified operation.",
                Result = result, CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException)
        {
            Update(id, x => x with { Status = "cancelled",
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
