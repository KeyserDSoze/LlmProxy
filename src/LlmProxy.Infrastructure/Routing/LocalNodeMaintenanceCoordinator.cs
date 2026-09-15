using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Infrastructure.Routing;

public sealed class LocalNodeMaintenanceCoordinator(IRequestLoadTracker loadTracker) : INodeMaintenanceCoordinator
{
    private readonly ConcurrentDictionary<Guid, byte> _blockedNodes = new();

    public bool IsAdmissionBlocked(Guid nodeId) => _blockedNodes.ContainsKey(nodeId);

    public ValueTask<bool> TryBeginDrainAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blockedNodes[nodeId] = 0;
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> TryConfirmDrainAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blockedNodes[nodeId] = 0;
        return ValueTask.FromResult(true);
    }

    public ValueTask CancelPendingDrainAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blockedNodes.TryRemove(nodeId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<NodeMaintenanceStatus> GetStatusAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new NodeMaintenanceStatus(
            CoordinationAvailable: true,
            AdmissionBlocked: IsAdmissionBlocked(nodeId),
            ActiveRequests: loadTracker.GetNodeActive(nodeId),
            Provider: "local"));
    }

    public ValueTask<bool> TryResumeAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _blockedNodes.TryRemove(nodeId, out _);
        return ValueTask.FromResult(true);
    }
}
