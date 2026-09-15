namespace LlmProxy.Application.Abstractions;

public sealed record NodeMaintenanceStatus(
    bool CoordinationAvailable,
    bool AdmissionBlocked,
    long ActiveRequests,
    string Provider,
    string? Error = null)
{
    public bool Drained => CoordinationAvailable && AdmissionBlocked && ActiveRequests == 0;
}

public interface INodeMaintenanceCoordinator
{
    ValueTask<bool> TryBeginDrainAsync(Guid nodeId, CancellationToken cancellationToken = default);
    ValueTask<bool> TryConfirmDrainAsync(Guid nodeId, CancellationToken cancellationToken = default);
    ValueTask CancelPendingDrainAsync(Guid nodeId, CancellationToken cancellationToken = default);
    ValueTask<NodeMaintenanceStatus> GetStatusAsync(Guid nodeId, CancellationToken cancellationToken = default);
    ValueTask<bool> TryResumeAsync(Guid nodeId, CancellationToken cancellationToken = default);
}
