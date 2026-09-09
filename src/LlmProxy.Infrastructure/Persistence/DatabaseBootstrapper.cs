using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Persistence;

public sealed class DatabaseBootstrapper(
    GatewayDbContext dbContext,
    IConfiguration configuration,
    ApiKeyHasher apiKeyHasher)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);

        if (configuration.GetValue("Bootstrap:Enabled", true) && !await dbContext.Nodes.AnyAsync(cancellationToken))
        {
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
        }

        var bootstrapApiKey = configuration["Authentication:ApiKey"];
        if (!string.IsNullOrWhiteSpace(bootstrapApiKey) && !await dbContext.ApiCredentials.AnyAsync(cancellationToken))
        {
            dbContext.ApiCredentials.Add(new ApiCredential(
                "Bootstrap / GitHub Copilot",
                ApiKeyHasher.GetPrefix(bootstrapApiKey),
                apiKeyHasher.Hash(bootstrapApiKey)));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
