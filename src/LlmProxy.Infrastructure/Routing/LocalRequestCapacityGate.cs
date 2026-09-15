using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Observability;

namespace LlmProxy.Infrastructure.Routing;

public sealed class LocalRequestCapacityGate(IRequestLoadTracker loadTracker) : IRequestCapacityGate
{
    public ValueTask<CapacityAdmissionResult> TryAcquireAsync(
        Guid deploymentId,
        Guid nodeId,
        int deploymentMaxConcurrency,
        int nodeMaxConcurrency,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = LlmProxyActivity.Start("llmproxy.capacity.acquire");
        activity?.SetTag("llmproxy.capacity.provider", "local");
        LlmProxyActivity.SetGuid(activity, "llmproxy.deployment.id", deploymentId);
        LlmProxyActivity.SetGuid(activity, "llmproxy.node.id", nodeId);
        activity?.SetTag("llmproxy.capacity.deployment_limit", deploymentMaxConcurrency);
        activity?.SetTag("llmproxy.capacity.node_limit", nodeMaxConcurrency);

        if (!loadTracker.TryEnter(
                deploymentId,
                nodeId,
                deploymentMaxConcurrency,
                nodeMaxConcurrency,
                out var lease) || lease is null)
        {
            activity?.SetTag("llmproxy.capacity.result", "rejected");
            LlmProxyActivity.MarkError(activity, "capacity_exhausted");
            return ValueTask.FromResult(CapacityAdmissionResult.Rejected("local"));
        }

        activity?.SetTag("llmproxy.capacity.result", "acquired");
        return ValueTask.FromResult(CapacityAdmissionResult.Success(new AsyncLease(lease), "local"));
    }

    private sealed class AsyncLease(IDisposable inner) : IRequestCapacityLease
    {
        private int _disposed;

        public CancellationToken CoordinationLost => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
