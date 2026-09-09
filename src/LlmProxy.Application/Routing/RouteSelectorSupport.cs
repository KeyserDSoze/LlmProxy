using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Application.Routing;

internal static class RouteSelectorSupport
{
    public static IReadOnlyList<DeploymentCandidate> Eligible(
        IReadOnlyList<DeploymentCandidate> candidates,
        IRequestLoadTracker loadTracker) =>
        candidates
            .Where(candidate => candidate.NodeStatus is NodeStatus.Healthy or NodeStatus.Degraded or NodeStatus.Unknown)
            .Where(candidate => loadTracker.GetActive(candidate.DeploymentId) < candidate.MaxConcurrency)
            .OrderBy(candidate => candidate.NodeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DeploymentId)
            .ToArray();

    public static RouteSelection ToSelection(DeploymentCandidate selected) =>
        new(
            selected.DeploymentId,
            selected.NodeId,
            selected.NodeName,
            selected.BaseAddress,
            selected.PublicModelName,
            selected.ProviderModelName,
            selected.MaxConcurrency);
}
