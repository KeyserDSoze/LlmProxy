using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Persistence;

public sealed class DatabaseBootstrapper(GatewayDbContext dbContext, IConfiguration configuration)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        if (!configuration.GetValue("Bootstrap:Enabled", true) || await dbContext.Nodes.AnyAsync(cancellationToken))
        {
            return;
        }

        var node = new InferenceNode(
            configuration["Bootstrap:NodeName"] ?? "dgx-01",
            configuration["Bootstrap:NodeBaseAddress"] ?? "http://localhost:8000",
            configuration.GetValue("Bootstrap:NodeWeight", 1),
            configuration.GetValue("Bootstrap:NodeMaxConcurrency", 4));

        var model = new ModelDefinition(
            configuration["Bootstrap:PublicModelName"] ?? "agic-code-fast",
            configuration["Bootstrap:ProviderModelName"] ?? "bootstrap-model",
            supportsStreaming: true,
            supportsTools: true);

        var deployment = new ModelDeployment(node.Id, model.Id);

        dbContext.Nodes.Add(node);
        dbContext.Models.Add(model);
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
