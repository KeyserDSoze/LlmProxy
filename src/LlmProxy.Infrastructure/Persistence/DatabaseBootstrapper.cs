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
using LlmProxy.Infrastructure.Retention;
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
    UpstreamCredentialProtector upstreamCredentialProtector,
    SensitiveDataProtector sensitiveDataProtector,
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
            var upstreamBearerToken = configuration["Bootstrap:NodeBearerToken"];
            if (!string.IsNullOrWhiteSpace(upstreamBearerToken))
            {
                node.SetUpstreamBearerTokenCiphertext(upstreamCredentialProtector.Protect(upstreamBearerToken));
            }

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
        if (!string.IsNullOrWhiteSpace(bootstrapApiKey))
        {
            var bootstrapHash = apiKeyHasher.Hash(bootstrapApiKey);
            var bootstrapCredential = await dbContext.ApiCredentials
                .SingleOrDefaultAsync(item => item.KeyHash == bootstrapHash, cancellationToken);

            if (bootstrapCredential is null && !await dbContext.ApiCredentials.AnyAsync(cancellationToken))
            {
                bootstrapCredential = new ApiCredential(
                    "Bootstrap / GitHub Copilot",
                    ApiKeyHasher.GetPrefix(bootstrapApiKey),
                    bootstrapHash);
                dbContext.ApiCredentials.Add(bootstrapCredential);
            }

            if (bootstrapCredential is not null && string.IsNullOrWhiteSpace(bootstrapCredential.SecretCiphertext))
            {
                bootstrapCredential.SetSecretCiphertext(
                    sensitiveDataProtector.Protect(bootstrapApiKey, $"api-credential:{bootstrapCredential.Id}"));
            }
        }

        if (!await dbContext.ContentLogSettings.AnyAsync(cancellationToken))
        {
            dbContext.ContentLogSettings.Add(new ContentLogSettingsRecord
            {
                Id = ContentLogSettingsRecord.SingletonId,
                RetentionDays = ContentLogRetentionService.DefaultRetentionDays,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        if (!await dbContext.UserAccessSettings.AnyAsync(cancellationToken))
        {
            dbContext.UserAccessSettings.Add(new UserAccessSettingsRecord
            {
                Id = UserAccessSettingsRecord.SingletonId,
                ProvisioningMode = UserAccessSettingsRecord.ManualMode,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        if (!await dbContext.ProductUpdatePolicies.AnyAsync(cancellationToken))
        {
            dbContext.ProductUpdatePolicies.Add(new ProductUpdatePolicyRecord
            {
                Id = ProductUpdatePolicyRecord.SingletonId,
                Mode = ProductUpdatePolicyRecord.ManualMode,
                TimeZoneId = "UTC",
                LocalHour = 2,
                LocalMinute = 0,
                DayOfWeek = 0,
                DayOfMonth = 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        // Compatibility bridge for installations that configured System One before it became
        // a first-class routed workload. Once imported, administrators manage it like any model.
        if (configuration.GetValue<bool>("SystemOne:Enabled") &&
            !await dbContext.Models.AnyAsync(item => item.Surface == ModelSurface.SystemOne, cancellationToken))
        {
            var legacyBaseAddress = configuration["SystemOne:BaseAddress"];
            if (!string.IsNullOrWhiteSpace(legacyBaseAddress))
            {
                var desiredNodeName = configuration["SystemOne:NodeName"] ?? "systemone-classifier";
                var nodeName = desiredNodeName;
                for (var suffix = 2; await dbContext.Nodes.AnyAsync(item => item.Name == nodeName, cancellationToken); suffix++)
                {
                    nodeName = $"{desiredNodeName}-{suffix}";
                }

                var node = new InferenceNode(
                    nodeName,
                    legacyBaseAddress,
                    weight: 1,
                    maxConcurrency: Math.Clamp(configuration.GetValue("SystemOne:MaxConcurrency", 8), 1, 1024));
                var legacyApiKey = configuration["SystemOne:ApiKey"];
                if (!string.IsNullOrWhiteSpace(legacyApiKey))
                {
                    node.SetUpstreamBearerTokenCiphertext(upstreamCredentialProtector.Protect(legacyApiKey));
                }

                var desiredPublicName = configuration["SystemOne:DefaultModel"] ?? "systemone-default";
                var publicName = desiredPublicName;
                for (var suffix = 2; await dbContext.Models.AnyAsync(item => item.PublicName == publicName, cancellationToken); suffix++)
                {
                    publicName = $"{desiredPublicName}-{suffix}";
                }

                var classifier = new ModelDefinition(
                    publicName,
                    configuration["SystemOne:ProviderModelName"] ?? "systemone-classifier",
                    supportsStreaming: false,
                    supportsTools: false,
                    surface: ModelSurface.SystemOne);

                dbContext.Nodes.Add(node);
                dbContext.Models.Add(classifier);
                dbContext.Deployments.Add(new ModelDeployment(node.Id, classifier.Id));
            }
        }

        var existingUsers = await dbContext.PlatformUsers
            .AsNoTracking()
            .Select(item => new { item.TenantId, item.ObjectId })
            .ToListAsync(cancellationToken);
        var existingUserKeys = existingUsers
            .Select(item => $"{item.TenantId}|{item.ObjectId}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ownedCredentials = await dbContext.ApiCredentials.AsNoTracking()
            .Where(item => item.OwnerTenantId != null && item.OwnerObjectId != null)
            .GroupBy(item => new { item.OwnerTenantId, item.OwnerObjectId })
            .Select(grouping => new
            {
                TenantId = grouping.Key.OwnerTenantId!,
                ObjectId = grouping.Key.OwnerObjectId!,
                PrincipalName = grouping.Max(item => item.OwnerPrincipalName),
                CreatedAtUtc = grouping.Min(item => item.CreatedAtUtc),
                LastSeenAtUtc = grouping.Max(item => item.LastUsedAtUtc)
            })
            .ToListAsync(cancellationToken);

        foreach (var owner in ownedCredentials)
        {
            if (!existingUserKeys.Add($"{owner.TenantId}|{owner.ObjectId}"))
            {
                continue;
            }

            dbContext.PlatformUsers.Add(new PlatformUserRecord
            {
                TenantId = owner.TenantId,
                ObjectId = owner.ObjectId,
                PrincipalName = owner.PrincipalName,
                Enabled = true,
                ProvisioningSource = "migration",
                CreatedAtUtc = owner.CreatedAtUtc,
                LastSeenAtUtc = owner.LastSeenAtUtc
            });
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
        var groupRatePolicySnapshots = (await dbContext.UsageGroupRateLimitPolicies.AsNoTracking().ToListAsync(cancellationToken))
            .Select(RateLimitPolicyRuntimeStateInterceptor.ToSnapshot);
        var ratePolicySnapshots = credentialRatePolicySnapshots
            .Concat(userRatePolicySnapshots)
            .Concat(groupRatePolicySnapshots)
            .ToArray();
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
