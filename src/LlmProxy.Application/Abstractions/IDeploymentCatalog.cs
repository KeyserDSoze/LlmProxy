using LlmProxy.Application.Routing;

namespace LlmProxy.Application.Abstractions;

public interface IDeploymentCatalog
{
    Task<IReadOnlyList<DeploymentCandidate>> GetCandidatesAsync(string publicModelName, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublicModel>> GetPublicModelsAsync(CancellationToken cancellationToken);
}
