using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmProxy.UpdateAgent;

public sealed partial class UpdateScheduler(
    UpdateAgentOptions options,
    ILogger<UpdateScheduler> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateJob? _activeJob;
    private List<UpdateJob> _recentJobs = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.StateDirectory);
        await LoadAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public async Task<UpdateAgentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new(ReadInstalledVersion(), _activeJob, _recentJobs.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<UpdateJob> ScheduleAsync(ScheduleUpdateRequest request, CancellationToken cancellationToken)
    {
        var version = NormalizeVersion(request.Version);
        var upgradePath = NormalizeUpgradePath(request.Versions, version);
        var scheduled = request.ScheduledForUtc?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        if (scheduled < DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            throw new ArgumentException("Scheduled time cannot be in the past.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeJob?.Status == "Running")
            {
                throw new InvalidOperationException("An update is already running.");
            }

            if (_activeJob?.Status == "Pending")
            {
                if (!request.Force)
                {
                    throw new InvalidOperationException("An update is already scheduled. Cancel it or use force to replace it.");
                }

                Archive(_activeJob with
                {
                    Status = "Cancelled",
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Error = "Replaced by a forced update request."
                });
            }

            _activeJob = new UpdateJob(
                Guid.NewGuid(),
                version,
                DateTimeOffset.UtcNow,
                scheduled,
                "Pending",
                UpgradePath: upgradePath);
            await SaveLockedAsync(cancellationToken);
            return _activeJob;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeJob is null || _activeJob.Id != id || _activeJob.Status != "Pending")
            {
                return false;
            }

            Archive(_activeJob with
            {
                Status = "Cancelled",
                CompletedAtUtc = DateTimeOffset.UtcNow
            });
            _activeJob = null;
            await SaveLockedAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            UpdateJob? due = null;
            await _gate.WaitAsync(stoppingToken);
            try
            {
                if (_activeJob is { Status: "Pending" } pending &&
                    pending.ScheduledForUtc <= DateTimeOffset.UtcNow)
                {
                    due = pending with { Status = "Running", StartedAtUtc = DateTimeOffset.UtcNow };
                    _activeJob = due;
                    await SaveLockedAsync(stoppingToken);
                }
            }
            finally
            {
                _gate.Release();
            }

            if (due is not null)
            {
                await ExecuteUpdateAsync(due, stoppingToken);
            }
        }
    }

    private async Task ExecuteUpdateAsync(UpdateJob job, CancellationToken stoppingToken)
    {
        UpdateJob completed;
        try
        {
            if (!File.Exists(options.BootstrapPath))
            {
                throw new FileNotFoundException("LlmProxy bootstrap helper is missing.", options.BootstrapPath);
            }

            var path = job.UpgradePath is { Count: > 0 } ? job.UpgradePath : [job.Version];
            foreach (var stepVersion in path)
            {
                await SetCurrentStepAsync(job.Id, stepVersion, stoppingToken);
                await ExecuteVersionAsync(stepVersion, stoppingToken);
            }

            completed = job with
            {
                Status = "Succeeded",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ExitCode = 0,
                CurrentStep = null
            };
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(exception, "LlmProxy update to {Version} failed.", job.Version);
            completed = job with
            {
                Status = "Failed",
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Error = Truncate(exception.Message, 1200)
            };
        }

        await _gate.WaitAsync(stoppingToken);
        try
        {
            Archive(completed);
            _activeJob = null;
            await SaveLockedAsync(stoppingToken);
        }
        finally
        {
            _gate.Release();
        }

        if (string.Equals(completed.Status, "Succeeded", StringComparison.Ordinal))
        {
            RequestSelfRestart();
        }
    }

    private async Task ExecuteVersionAsync(string version, CancellationToken stoppingToken)
    {
        var startInfo = new ProcessStartInfo(options.BootstrapPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(version);
        startInfo.ArgumentList.Add("--skip-docker-install");
        startInfo.ArgumentList.Add("--skip-node-check");
        startInfo.ArgumentList.Add("--non-interactive");
        startInfo.ArgumentList.Add("--upgrade");
        startInfo.Environment["LLMPROXY_UPDATE_AGENT_ACTIVE"] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LlmProxy bootstrap helper.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.CommandTimeoutMinutes));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var detail = LastText(stderr, stdout);
            throw new InvalidOperationException(
                $"Update to {version} exited with code {process.ExitCode}.{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}")}");
        }
    }

    private async Task SetCurrentStepAsync(Guid jobId, string version, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activeJob?.Id == jobId)
            {
                _activeJob = _activeJob with { CurrentStep = version };
                await SaveLockedAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RequestSelfRestart()
    {
        try
        {
            var startInfo = new ProcessStartInfo("systemd-run")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--quiet");
            startInfo.ArgumentList.Add("--collect");
            startInfo.ArgumentList.Add($"--unit=llmproxy-update-agent-refresh-{Guid.NewGuid():N}");
            startInfo.ArgumentList.Add("--on-active=2s");
            startInfo.ArgumentList.Add("systemctl");
            startInfo.ArgumentList.Add("restart");
            startInfo.ArgumentList.Add("llmproxy-update-agent.service");
            _ = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Update completed but the Update Agent could not schedule its own service restart. The new binary will load on the next service restart.");
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var path = StatePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<PersistedState>(stream, JsonOptions, cancellationToken);
            _activeJob = state?.ActiveJob;
            _recentJobs = state?.RecentJobs ?? [];

            if (_activeJob?.Status == "Running")
            {
                Archive(_activeJob with
                {
                    Status = "Failed",
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    Error = "Update agent restarted while the update was running. Verify the installed version and host logs."
                });
                _activeJob = null;
                await SaveLockedAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not read persisted update-agent state; starting with empty state.");
            _activeJob = null;
            _recentJobs = [];
        }
    }

    private async Task SaveLockedAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.StateDirectory);
        var temp = StatePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new PersistedState(_activeJob, _recentJobs),
                JsonOptions,
                cancellationToken);
        }
        File.Move(temp, StatePath, overwrite: true);
    }

    private void Archive(UpdateJob job)
    {
        _recentJobs.Insert(0, job);
        if (_recentJobs.Count > 20)
        {
            _recentJobs.RemoveRange(20, _recentJobs.Count - 20);
        }
    }

    private string? ReadInstalledVersion()
    {
        var path = Path.Combine(options.InstallDirectory, "current", "VERSION");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static string NormalizeVersion(string raw)
    {
        var value = raw.Trim().TrimStart('v');
        if (!StableVersionRegex().IsMatch(value))
        {
            throw new ArgumentException("Version must be a stable MAJOR.MINOR.PATCH release.", nameof(raw));
        }
        return value;
    }

    private static IReadOnlyList<string> NormalizeUpgradePath(IReadOnlyList<string>? requested, string target)
    {
        var path = (requested is { Count: > 0 } ? requested : [target])
            .Select(NormalizeVersion)
            .ToArray();
        if (!string.Equals(path[^1], target, StringComparison.Ordinal))
        {
            throw new ArgumentException("The final upgrade-path version must match the requested target version.", nameof(requested));
        }

        Version? previous = null;
        foreach (var item in path)
        {
            var parsed = Version.Parse(item);
            if (previous is not null && parsed <= previous)
            {
                throw new ArgumentException("Upgrade-path versions must be strictly increasing.", nameof(requested));
            }
            previous = parsed;
        }

        return path;
    }

    private static string LastText(params string[] values)
    {
        var combined = string.Join("\n", values.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return Truncate(combined, 1200);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[^maxLength..];

    private string StatePath => Path.Combine(options.StateDirectory, "state.json");

    private sealed record PersistedState(UpdateJob? ActiveJob, List<UpdateJob> RecentJobs);

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionRegex();
}
