using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Routing;

public sealed class EfDeploymentCatalog(GatewayDbContext dbContext) : IDeploymentCatalog
{
    public async Task<IReadOnlyList<DeploymentCandidate>> GetCandidatesAsync(
        string publicModelName,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from deployment in dbContext.Deployments.AsNoTracking()
            join node in dbContext.Nodes.AsNoTracking() on deployment.NodeId equals node.Id
            join model in dbContext.Models.AsNoTracking() on deployment.ModelId equals model.Id
            where deployment.Enabled && node.Enabled && model.Enabled && model.PublicName == publicModelName
            select new
            {
                deployment.Id,
                deployment.Weight,
                DeploymentMaxConcurrency = deployment.MaxConcurrency,
                NodeId = node.Id,
                NodeName = node.Name,
                node.BaseAddress,
                node.Status,
                NodeMaxConcurrency = node.MaxConcurrency,
                ModelId = model.Id,
                model.PublicName,
                model.ProviderModelName
            }).ToListAsync(cancellationToken);

        return rows
            .Select(row => new DeploymentCandidate(
                row.Id,
                row.NodeId,
                row.NodeName,
                row.BaseAddress,
                row.ModelId,
                row.PublicName,
                row.ProviderModelName,
                row.Weight,
                row.DeploymentMaxConcurrency ?? row.NodeMaxConcurrency,
                row.Status))
            .ToArray();
    }

    public async Task<IReadOnlyList<PublicModel>> GetPublicModelsAsync(CancellationToken cancellationToken)
        => await dbContext.Models
            .AsNoTracking()
            .Where(model => model.Enabled)
            .OrderBy(model => model.PublicName)
            .Select(model => new PublicModel(model.Id, model.PublicName, model.SupportsStreaming, model.SupportsTools))
            .ToListAsync(cancellationToken);
}
