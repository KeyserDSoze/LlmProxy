using System.Collections.Concurrent;

namespace LlmProxy.Api.Admin;

public enum AdminOperationStatus { Running, Succeeded, Failed }

public sealed record AdminOperation(
    Guid Id,
    string Kind,
    string Title,
    Guid? NodeId,
    Guid? DeploymentId,
    AdminOperationStatus Status,
    string Detail,
    string? Error,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    object? Data = null);

/// <summary>What a finished operation reports back to the operator.</summary>
public sealed record AdminOperationOutcome(bool Succeeded, string Message, object? Data = null);

/// <summary>
/// Runs administrative actions that talk to a server Agent (stop, force stop, uninstall) outside the HTTP request.
/// The browser gets an id immediately, so a slow Agent or a proxy that cuts long requests cannot hide the result; the
/// outcome stays readable here and in the audit log. State is per replica and in memory: an operation only lasts
/// seconds to minutes, and routing was already made safe before the Agent is asked to do anything.
/// </summary>
public sealed class AdminOperationService(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<AdminOperationService> logger)
{
    private const int MaxRetained = 100;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, AdminOperation> _operations = new();

    public IReadOnlyList<AdminOperation> List() =>
        _operations.Values.OrderByDescending(operation => operation.StartedAtUtc).ToArray();

    public AdminOperation? Get(Guid id) => _operations.GetValueOrDefault(id);

    public AdminOperation? RunningFor(Guid deploymentId) =>
        _operations.Values.FirstOrDefault(operation => operation.DeploymentId == deploymentId && operation.Status == AdminOperationStatus.Running);

    /// <summary>Starts the work and returns at once. Returns null when this deployment already has an operation running.</summary>
    public AdminOperation? Start(string kind, string title, Guid? nodeId, Guid? deploymentId,
        Func<IServiceProvider, CancellationToken, Task<AdminOperationOutcome>> work)
    {
        lock (_operations)
        {
            if (deploymentId is { } id && RunningFor(id) is not null) return null;
            var operation = new AdminOperation(Guid.NewGuid(), kind, title, nodeId, deploymentId,
                AdminOperationStatus.Running, "Queued", null, DateTimeOffset.UtcNow, null);
            _operations[operation.Id] = operation;
            Trim();
            _ = Task.Run(() => ExecuteAsync(operation, work));
            return operation;
        }
    }

    private async Task ExecuteAsync(AdminOperation operation, Func<IServiceProvider, CancellationToken, Task<AdminOperationOutcome>> work)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(MaxDuration);
        try
        {
            using var scope = scopes.CreateScope();
            var outcome = await work(scope.ServiceProvider, timeout.Token);
            Finish(operation, outcome.Succeeded ? AdminOperationStatus.Succeeded : AdminOperationStatus.Failed,
                outcome.Succeeded ? outcome.Message : "Failed", outcome.Succeeded ? null : outcome.Message, outcome.Data);
        }
        catch (OperationCanceledException)
        {
            Finish(operation, AdminOperationStatus.Failed, "Failed",
                lifetime.ApplicationStopping.IsCancellationRequested
                    ? "The gateway restarted before this finished. Routing was already disabled; check the model and retry."
                    : $"It did not finish within {MaxDuration.TotalMinutes:0} minutes. Check the Agent and retry.");
        }
        catch (Exception error)
        {
            logger.LogError(error, "Administrative operation {Kind} {OperationId} failed", operation.Kind, operation.Id);
            Finish(operation, AdminOperationStatus.Failed, "Failed", "Unexpected error: " + error.Message);
        }
    }

    private void Finish(AdminOperation operation, AdminOperationStatus status, string detail, string? error, object? data = null) =>
        _operations[operation.Id] = operation with
        {
            Status = status, Detail = detail, Error = error is { Length: > 1500 } ? error[..1500] : error,
            FinishedAtUtc = DateTimeOffset.UtcNow, Data = data
        };

    private void Trim()
    {
        if (_operations.Count <= MaxRetained) return;
        foreach (var stale in _operations.Values.Where(operation => operation.Status != AdminOperationStatus.Running)
                     .OrderBy(operation => operation.StartedAtUtc).Take(_operations.Count - MaxRetained))
            _operations.TryRemove(stale.Id, out _);
    }
}
