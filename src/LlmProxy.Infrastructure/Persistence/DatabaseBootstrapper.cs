using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Governance;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.Infrastructure.Persistence;

public sealed class DatabaseBootstrapper(
    GatewayDbContext dbContext,
    IConfiguration configuration,
    ApiKeyHasher apiKeyHasher,
    IApiCredentialCache credentialCache,
    IRouteCatalog routeCatalog,
    IRuntimeStateEventSink runtimeStateSink,
    RoutingStrategyState routingStrategyState,
    RoutingTuningState routingTuningState,
    RequestRateLimiter requestRateLimiter)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.MigrateAsync(cancellationToken);

        var configuredStrategy = ParseConfiguredStrategy(configuration["Routing:Strategy"]);
        var routingPolicy = await dbContext.RoutingPolicies.SingleOrDefaultAsync(item => item.Id == RoutingPolicy.SingletonId, cancellationToken);
        if (routingPolicy is null)
        {
            routingPolicy = new RoutingPolicy(configuredStrategy);
            dbContext.RoutingPolicies.Add(routingPolicy);
        }
        routingStrategyState.Set(routingPolicy.Strategy);

        var tuningPolicy = await dbContext.RoutingTuningPolicies.SingleOrDefaultAsync(item => item.Id == RoutingTuningPolicy.SingletonId, cancellationToken);
        if (tuningPolicy is null)
        {
            tuningPolicy = new RoutingTuningPolicy(RoutingTuningSettings.Default);
            dbContext.RoutingTuningPolicies.Add(tuningPolicy);
        }
        routingTuningState.Set(tuningPolicy.ToSettings());

        if (configuration.GetValue("Bootstrap:Enabled", true) && !await dbContext.Nodes.AnyAsync(cancellationToken))
        {
            var node = new InferenceNode(
                configuration["Bootstrap:NodeName"] ?? "dgx-01",
                configuration["Bootstrap:NodeBaseAddress"] ?? "http://localhost:8000",
                configuration.GetValue("Bootstrap:NodeWeight", 1),
                configuration.GetValue("Bootstrap:NodeMaxConcurrency", 4));
            node.SetHardwareMetricsBaseAddress(configuration["Bootstrap:HardwareMetricsBaseAddress"]);

            var model = new ModelDefinition(
                configuration["Bootstrap:PublicModelName"] ?? "agic-code-fast",
                configuration["Bootstrap:ProviderModelName"] ?? "bootstrap-model",
                supportsStreaming: true,
                supportsTools: true);

            dbContext.Nodes.Add(node);
            dbContext.Models.Add(model);
            dbContext.Deployments.Add(new ModelDeployment(node.Id, model.Id));
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

        var credentialSnapshots = (await dbContext.ApiCredentials.AsNoTracking().ToListAsync(cancellationToken))
            .Select(ApiCredentialSnapshot.From)
            .ToArray();
        credentialCache.Replace(credentialSnapshots);
        runtimeStateSink.PublishCredentialSnapshot(credentialSnapshots);

        var nodeSnapshots = (await dbContext.Nodes.AsNoTracking().ToListAsync(cancellationToken)).Select(RouteNodeSnapshot.From).ToArray();
        var modelSnapshots = (await dbContext.Models.AsNoTracking().ToListAsync(cancellationToken)).Select(RouteModelSnapshot.From).ToArray();
        var deploymentSnapshots = (await dbContext.Deployments.AsNoTracking().ToListAsync(cancellationToken)).Select(RouteDeploymentSnapshot.From).ToArray();
        routeCatalog.Replace(nodeSnapshots, modelSnapshots, deploymentSnapshots);
        runtimeStateSink.PublishRouteCatalogSnapshot(nodeSnapshots, modelSnapshots, deploymentSnapshots);

        var credentialRatePolicySnapshots = (await dbContext.RateLimitPolicies.AsNoTracking().ToListAsync(cancellationToken))
            .Select(RateLimitPolicyRuntimeStateInterceptor.ToSnapshot);
        var userRatePolicySnapshots = (await dbContext.UserRateLimitPolicies.AsNoTracking().ToListAsync(cancellationToken))
            .Select(RateLimitPolicyRuntimeStateInterceptor.ToSnapshot);
        var ratePolicySnapshots = credentialRatePolicySnapshots.Concat(userRatePolicySnapshots).ToArray();
        requestRateLimiter.ReplacePolicies(ratePolicySnapshots);
        runtimeStateSink.PublishRatePolicySnapshot(ratePolicySnapshots);
    }

    private static RoutingStrategy ParseConfiguredStrategy(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? nameof(RoutingStrategy.WeightedLeastLoaded) : value.Trim();
        return Enum.TryParse<RoutingStrategy>(raw, ignoreCase: true, out var strategy) && Enum.IsDefined(strategy)
            ? strategy
            : throw new InvalidOperationException($"Unsupported Routing:Strategy '{raw}'. Supported values: {string.Join(", ", Enum.GetNames<RoutingStrategy>())}.");
    }
}
