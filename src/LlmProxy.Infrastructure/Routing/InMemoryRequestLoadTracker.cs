using LlmProxy.Application.Abstractions;

namespace LlmProxy.Infrastructure.Routing;

public sealed class InMemoryRequestLoadTracker : IRequestLoadTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _deploymentActive = [];
    private readonly Dictionary<Guid, int> _nodeActive = [];

    public int GetActive(Guid deploymentId)
    {
        lock (_gate)
        {
            return _deploymentActive.GetValueOrDefault(deploymentId);
        }
    }

    public int GetNodeActive(Guid nodeId)
    {
        lock (_gate)
        {
            return _nodeActive.GetValueOrDefault(nodeId);
        }
    }

    public IDisposable Enter(Guid deploymentId)
    {
        lock (_gate)
        {
            _deploymentActive[deploymentId] = _deploymentActive.GetValueOrDefault(deploymentId) + 1;
        }

        return new LegacyLease(this, deploymentId);
    }

    public bool TryEnter(
        Guid deploymentId,
        Guid nodeId,
        int deploymentMaxConcurrency,
        int nodeMaxConcurrency,
        out IDisposable? lease)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(deploymentMaxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeMaxConcurrency, 1);

        lock (_gate)
        {
            var deploymentActive = _deploymentActive.GetValueOrDefault(deploymentId);
            var nodeActive = _nodeActive.GetValueOrDefault(nodeId);
            if (deploymentActive >= deploymentMaxConcurrency || nodeActive >= nodeMaxConcurrency)
            {
                lease = null;
                return false;
            }

            _deploymentActive[deploymentId] = deploymentActive + 1;
            _nodeActive[nodeId] = nodeActive + 1;
            lease = new CapacityLease(this, deploymentId, nodeId);
            return true;
        }
    }

    private void ExitDeployment(Guid deploymentId)
    {
        lock (_gate)
        {
            Decrement(_deploymentActive, deploymentId);
        }
    }

    private void ExitCapacity(Guid deploymentId, Guid nodeId)
    {
        lock (_gate)
        {
            Decrement(_deploymentActive, deploymentId);
            Decrement(_nodeActive, nodeId);
        }
    }

    private static void Decrement(Dictionary<Guid, int> counts, Guid id)
    {
        if (!counts.TryGetValue(id, out var current))
        {
            return;
        }

        if (current <= 1)
        {
            counts.Remove(id);
        }
        else
        {
            counts[id] = current - 1;
        }
    }

    private sealed class LegacyLease(InMemoryRequestLoadTracker owner, Guid deploymentId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ExitDeployment(deploymentId);
            }
        }
    }

    private sealed class CapacityLease(InMemoryRequestLoadTracker owner, Guid deploymentId, Guid nodeId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ExitCapacity(deploymentId, nodeId);
            }
        }
    }
}
