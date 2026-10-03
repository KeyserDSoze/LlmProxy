using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Application.Routing;

internal static class RouteSelectorSupport
{
    public static IReadOnlyList<DeploymentCandidate> Eligible(
        IReadOnlyList<DeploymentCandidate> candidates,
        IRequestLoadTracker loadTracker) =>
        candidates
            .Where(IsOperational)
            .Where(candidate => HasDeploymentCapacity(candidate, loadTracker))
            .Where(candidate => HasNodeCapacity(candidate, loadTracker))
            .OrderBy(candidate => candidate.NodeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DeploymentId)
            .ToArray();

    public static bool IsOperational(DeploymentCandidate candidate)
        => candidate.NodeStatus is NodeStatus.Healthy or NodeStatus.Degraded or NodeStatus.Unknown;

    public static bool HasDeploymentCapacity(DeploymentCandidate candidate, IRequestLoadTracker loadTracker)
        => loadTracker.GetActive(candidate.DeploymentId) < candidate.MaxConcurrency;

    public static bool HasNodeCapacity(DeploymentCandidate candidate, IRequestLoadTracker loadTracker)
        => loadTracker.GetNodeActive(candidate.NodeId) < candidate.NodeMaxConcurrency;

    public static RouteSelection ToSelection(DeploymentCandidate selected) =>
        new(
            selected.DeploymentId,
            selected.NodeId,
            selected.NodeName,
            selected.BaseAddress,
            selected.PublicModelName,
            selected.ProviderModelName,
            selected.MaxConcurrency,
            selected.NodeMaxConcurrency,
            selected.UpstreamBearerTokenCiphertext,
            selected.Surface);
}
