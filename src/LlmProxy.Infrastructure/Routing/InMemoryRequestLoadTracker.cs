using System.Collections.Concurrent;
using LlmProxy.Application.Abstractions;

namespace LlmProxy.Infrastructure.Routing;

public sealed class InMemoryRequestLoadTracker : IRequestLoadTracker
{
    private readonly ConcurrentDictionary<Guid, int> _active = new();

    public int GetActive(Guid deploymentId)
        => _active.TryGetValue(deploymentId, out var count) ? count : 0;

    public IDisposable Enter(Guid deploymentId)
    {
        _active.AddOrUpdate(deploymentId, 1, static (_, current) => current + 1);
        return new Lease(this, deploymentId);
    }

    private void Exit(Guid deploymentId)
    {
        while (true)
        {
            if (!_active.TryGetValue(deploymentId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                _active.TryRemove(deploymentId, out _);
                return;
            }

            if (_active.TryUpdate(deploymentId, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed class Lease(InMemoryRequestLoadTracker owner, Guid deploymentId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit(deploymentId);
            }
        }
    }
}
