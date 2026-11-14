using LlmProxy.Application.Routing;
using LlmProxy.Domain.Models;

namespace LlmProxy.Application.Abstractions;

public interface IDeploymentCatalog
{
    Task<IReadOnlyList<DeploymentCandidate>> GetCandidatesAsync(string publicModelName, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublicModel>> GetPublicModelsAsync(CancellationToken cancellationToken, ModelSurface? surface = null);

    /// <summary>
    /// The models a client can use right now: enabled, with at least one enabled deployment on an enabled server that is
    /// not Unhealthy or Draining (the same test routing applies). Registered models that are stopped are left out, so
    /// <c>GET /v1/models</c> only lists what answers. Capacity is not considered: a busy model is still available.
    /// </summary>
    async Task<IReadOnlyList<PublicModel>> GetAvailableModelsAsync(CancellationToken cancellationToken, ModelSurface? surface = null)
    {
        var available = new List<PublicModel>();
        foreach (var model in await GetPublicModelsAsync(cancellationToken, surface))
        {
            var candidates = await GetCandidatesAsync(model.PublicName, cancellationToken);
            if (candidates.Any(RouteSelectorSupport.IsOperational)) available.Add(model);
        }
        return available;
    }
}
