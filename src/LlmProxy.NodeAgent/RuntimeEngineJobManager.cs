using System.Text.Json;

namespace LlmProxy.NodeAgent;

public sealed record RuntimeEngineJob(
    Guid Id, string EngineId, string EngineLabel, string Action, string Status, string Detail, string? Error, RuntimeEngineResult? Result,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc = null);

/// <summary>
/// Engine downloads and builds take minutes. They run on the Agent and persist, so closing the browser or losing the tunnel
/// does not cancel them. One at a time: two image pulls would only compete for bandwidth and disk.
/// </summary>
public sealed class RuntimeEngineJobManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _file;
    private readonly Dictionary<Guid, RuntimeEngineJob> _jobs;
    private readonly Dictionary<Guid, CancellationTokenSource> _active = [];
    private readonly RuntimeEngineService _engines;
    private readonly IHostApplicationLifetime _host;

    public RuntimeEngineJobManager(NodeAgentOptions options, RuntimeEngineService engines, IHostApplicationLifetime host,
        ILogger<RuntimeEngineJobManager>? logger = null)
    {
        _engines = engines;
        _host = host;
        Directory.CreateDirectory(options.DataDirectory);
        _file = Path.Combine(options.DataDirectory, "engine-jobs.json");
        _jobs = AgentStateFile.ReadList<RuntimeEngineJob>(_file, Json, logger).ToDictionary(job => job.Id, job =>
            job.Status is "queued" or "running"
                ? job with { Status = "interrupted", Detail = "The Agent restarted during the operation.", Error = "Run it again.", CompletedAtUtc = DateTimeOffset.UtcNow }
                : job);
        Persist();
    }

    public IReadOnlyList<RuntimeEngineJob> List()
    {
        lock (_gate) return _jobs.Values.OrderByDescending(job => job.CreatedAtUtc).Take(50).ToArray();
    }

    public RuntimeEngineJob Enqueue(string engineId, string action)
    {
        if (RuntimeEngineCatalog.Find(engineId) is null) throw new ArgumentException($"Unknown engine '{engineId}'.", nameof(engineId));
        if (action is not ("install" or "update")) throw new ArgumentException("The action must be install or update.", nameof(action));
        lock (_gate)
        {
            if (_jobs.Values.FirstOrDefault(job => job.Status is "queued" or "running") is { } busy)
                throw new InvalidOperationException($"Another engine operation is running ({busy.Action} {busy.EngineId}). Wait for it to finish.");
            foreach (var old in _jobs.Values.Where(job => job.Status is not ("queued" or "running"))
                .OrderBy(job => job.CreatedAtUtc).Take(Math.Max(0, _jobs.Count - 99)).ToArray())
                _jobs.Remove(old.Id);
            var created = new RuntimeEngineJob(Guid.NewGuid(), engineId, RuntimeEngineCatalog.Find(engineId)!.Label, action, "queued", "Queued on the Agent.", null, null, DateTimeOffset.UtcNow);
            _jobs.Add(created.Id, created);
            Persist();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_host.ApplicationStopping);
            _active.Add(created.Id, cts);
            _ = Task.Run(() => ExecuteAsync(created.Id, cts));
            return created;
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var cts)) return false;
            cts.Cancel();
            return true;
        }
    }

    private async Task ExecuteAsync(Guid id, CancellationTokenSource cts)
    {
        try
        {
            var job = Get(id)!;
            Update(id, current => current with { Status = "running", Detail = "Starting…" });
            var last = DateTimeOffset.MinValue;
            var result = await _engines.InstallOrUpdateAsync(job.EngineId, job.Action, detail =>
            {
                if (DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(1)) return;
                last = DateTimeOffset.UtcNow;
                Update(id, current => current with { Detail = detail });
            }, cts.Token);
            Update(id, current => current with { Status = "completed", Detail = result.Message, Result = result, CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException)
        {
            Update(id, current => current with { Status = "cancelled", Detail = "Cancelled.", Error = "Cancelled before it finished.", CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception exception)
        {
            var error = exception.Message;
            Update(id, current => current with { Status = "failed", Detail = "The operation failed.", Error = error.Length > 3000 ? error[..3000] : error, CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        finally
        {
            lock (_gate) _active.Remove(id);
            cts.Dispose();
        }
    }

    private RuntimeEngineJob? Get(Guid id) { lock (_gate) return _jobs.GetValueOrDefault(id); }

    private void Update(Guid id, Func<RuntimeEngineJob, RuntimeEngineJob> transform)
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
        File.WriteAllText(temp, JsonSerializer.Serialize(_jobs.Values.OrderByDescending(job => job.CreatedAtUtc).Take(100), Json));
        File.Move(temp, _file, true);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
