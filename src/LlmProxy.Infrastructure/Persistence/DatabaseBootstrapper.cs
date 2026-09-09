using LlmProxy.Application.Routing;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Persistence;

public sealed class DatabaseBootstrapper(
    GatewayDbContext dbContext,
    IConfiguration configuration,
    ApiKeyHasher apiKeyHasher,
    RoutingStrategyState routingStrategyState)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);

        var configuredStrategy = ParseConfiguredStrategy(configuration["Routing:Strategy"]);
        var routingPolicy = await dbContext.RoutingPolicies.SingleOrDefaultAsync(
            item => item.Id == RoutingPolicy.SingletonId,
            cancellationToken);

        if (routingPolicy is null)
        {
            routingPolicy = new RoutingPolicy(configuredStrategy);
            dbContext.RoutingPolicies.Add(routingPolicy);
        }

        routingStrategyState.Set(routingPolicy.Strategy);

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

    private static RoutingStrategy ParseConfiguredStrategy(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? nameof(RoutingStrategy.WeightedLeastLoaded) : value.Trim();
        return Enum.TryParse<RoutingStrategy>(raw, ignoreCase: true, out var strategy) && Enum.IsDefined(strategy)
            ? strategy
            : throw new InvalidOperationException(
                $"Unsupported Routing:Strategy '{raw}'. Supported values: {string.Join(", ", Enum.GetNames<RoutingStrategy>())}.");
    }
}
