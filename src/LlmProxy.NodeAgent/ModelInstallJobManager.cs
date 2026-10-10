using System.Text.Json;

namespace LlmProxy.NodeAgent;

public sealed record TransferProgress(string Stage, double? Percent, string Detail, long? CompletedBytes, long? TotalBytes);

public sealed record ModelInstallJob(Guid Id, InstallRequest Request, string Status, string Stage,
    double? Percent, string Detail, long? CompletedBytes, long? TotalBytes,
    ManagedModelState? Result, string? Error, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc = null);

/// <summary>Persistent bounded model/image download jobs. Jobs outlive HTTP requests and browser sessions.</summary>
public sealed class ModelInstallJobManager
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ModelInstallJob> _jobs;
    private readonly Dictionary<Guid, CancellationTokenSource> _cancel = [];
    private readonly string _file;
    private DateTimeOffset _lastProgressPersistedUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _singleInstall = new(1, 1);
    private readonly DockerModelRuntimeManager _manager;
    private readonly IHostApplicationLifetime _host;

    public ModelInstallJobManager(NodeAgentOptions options, DockerModelRuntimeManager manager, IHostApplicationLifetime host)
    {
        _manager = manager;
        _host = host;
        Directory.CreateDirectory(options.DataDirectory);
        _file = Path.Combine(options.DataDirectory, "install-jobs.json");
        var restored = File.Exists(_file)
            ? JsonSerializer.Deserialize<List<ModelInstallJob>>(File.ReadAllText(_file), Json) ?? []
            : [];
        _jobs = restored.ToDictionary(x => x.Id, x => x.Status is "queued" or "running"
            ? x with { Status = "interrupted", Error = "Agent restarted; cached downloads are preserved. Retry installation.", CompletedAtUtc = DateTimeOffset.UtcNow }
            : x);
        Save();
    }

    public IReadOnlyList<ModelInstallJob> List()
    {
        lock (_sync) return _jobs.Values.OrderByDescending(x => x.CreatedAtUtc).Take(50).ToArray();
    }

    public ModelInstallJob? Get(Guid id) { lock (_sync) return _jobs.GetValueOrDefault(id); }

    public ModelInstallJob Enqueue(InstallRequest request)
    {
        ManagedRuntimeProfiles.Validate(request);
        lock (_sync)
        {
            if (_jobs.Values.Count(x => x.Status is "queued" or "running") >= 4)
                throw new InvalidOperationException("Four pending/running installation jobs are already queued.");
            if (_jobs.Count > 100)
            {
                foreach (var old in _jobs.Values.Where(x => x.Status is not ("queued" or "running"))
                    .OrderBy(x => x.CreatedAtUtc).Take(_jobs.Count - 100).ToArray())
                    _jobs.Remove(old.Id);
            }
            var job = new ModelInstallJob(Guid.NewGuid(), request, "queued", "queued",
                null, "Waiting for host installation slot", null, null, null, null, DateTimeOffset.UtcNow);
            _jobs.Add(job.Id, job);
            Save();
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_host.ApplicationStopping);
            _cancel.Add(job.Id, cancel);
            _ = Task.Run(() => ExecuteAsync(job.Id, cancel));
            return job;
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_sync)
        {
            if (!_cancel.TryGetValue(id, out var cancel)) return false;
            cancel.Cancel();
            return true;
        }
    }

    private async Task ExecuteAsync(Guid id, CancellationTokenSource cts)
    {
        try
        {
            await _singleInstall.WaitAsync(cts.Token);
            try
            {
                Update(id, j => j with { Status = "running", Stage = "preparing", Detail = "Preparing model installation" });
                var result = await _manager.InstallAsync(Get(id)!.Request, cts.Token, step =>
                    Update(id, j => j with
                    {
                        Stage = step.Stage,
                        Percent = step.Percent,
                        Detail = step.Detail.Length > 250 ? step.Detail[..250] : step.Detail,
                        CompletedBytes = step.CompletedBytes,
                        TotalBytes = step.TotalBytes
                    }));
                Update(id, j => j with { Status = "completed", Stage = "complete", Percent = 100,
                    Detail = "Model files ready; register deployment from Admin", Result = result,
                    CompletedAtUtc = DateTimeOffset.UtcNow });
            }
            finally { _singleInstall.Release(); }
        }
        catch (OperationCanceledException)
        {
            Update(id, j => j with { Status = "cancelled", Stage = "cancelled", Detail = "Download cancelled",
                CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            Update(id, j => j with { Status = "failed", Stage = "failed",
                Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
                Detail = "Installation failed", CompletedAtUtc = DateTimeOffset.UtcNow });
        }
        finally
        {
            lock (_sync) { _cancel.Remove(id, out _); }
            cts.Dispose();
        }
    }

    private void Update(Guid id, Func<ModelInstallJob, ModelInstallJob> update)
    {
        lock (_sync)
        {
            if (!_jobs.TryGetValue(id, out var original)) return;
            var next = update(original);
            if (next == original) return;
            // UI needs visible state, but there is no need to write every terminal carriage return.
            var stageChanged = original.Stage != next.Stage || original.Status != next.Status;
            var meaningfulPercent = original.Percent is double previous && next.Percent is double current
                ? Math.Abs(current - previous) >= 1 : original.Percent != next.Percent;
            if (!stageChanged && DateTimeOffset.UtcNow - _lastProgressPersistedUtc < TimeSpan.FromSeconds(1))
                return;
            if (!stageChanged && !meaningfulPercent && original.Detail == next.Detail) return;
            _jobs[id] = next;
            Save();
            _lastProgressPersistedUtc = DateTimeOffset.UtcNow;
        }
    }

    private void Save()
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_jobs.Values.OrderByDescending(x => x.CreatedAtUtc).Take(100), Json));
        File.Move(temp, _file, true);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(_file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
