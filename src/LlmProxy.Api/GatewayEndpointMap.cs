using LlmProxy.Api.Admin;
using LlmProxy.Api.Identity;
using LlmProxy.Api.Multimodal;
using LlmProxy.Api.OpenAi;
using LlmProxy.Api.Product;
using LlmProxy.Api.SystemOne;

namespace LlmProxy.Api;

/// <summary>
/// Every gateway, admin and self-service route in one place so the authorization contract can be enumerated by a test.
/// </summary>
internal static class GatewayEndpointMap
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        endpoints.MapOpenAiEndpoints();
        endpoints.MapSystemOneEndpoints();
        endpoints.MapSystemOneDefaultAdminEndpoints(entraEnabled);
        endpoints.MapMultimodalEndpoints();
        endpoints.MapRealtimeEndpoints();
        endpoints.MapIdentitySelfServiceEndpoints(entraEnabled);
        endpoints.MapIdentityAdminEndpoints(entraEnabled);
        endpoints.MapPlatformUserAdminEndpoints(entraEnabled);
        endpoints.MapPlatformUserGroupEndpoints(entraEnabled);
        endpoints.MapAdminEndpoints(entraEnabled);
        endpoints.MapCorsAdminEndpoints(entraEnabled);
        endpoints.MapNodeMaintenanceAdminEndpoints(entraEnabled);
        endpoints.MapCredentialRotationAdminEndpoints(entraEnabled);
        endpoints.MapCredentialGovernanceAdminEndpoints(entraEnabled);
        endpoints.MapApiCredentialSecretAdminEndpoints(entraEnabled);
        endpoints.MapContentLogsAdminEndpoints(entraEnabled);
        endpoints.MapTestingAdminEndpoints(entraEnabled);
        endpoints.MapPlaygroundChatStreamEndpoints(entraEnabled);
        endpoints.MapMetricsAdminEndpoints(entraEnabled);
        endpoints.MapRoutingTuningEndpoints(entraEnabled);
        endpoints.MapRouteCatalogAdminEndpoints(entraEnabled);
        endpoints.MapRuntimeStateAdminEndpoints(entraEnabled);
        endpoints.MapNodeHardwareMetricsEndpoints(entraEnabled);
        endpoints.MapNodeModelManagementEndpoints(entraEnabled);
        endpoints.MapRuntimeEngineAdminEndpoints(entraEnabled);
        endpoints.MapModelCapabilityProbeEndpoints(entraEnabled);
        endpoints.MapNodeEnrollmentEndpoints(entraEnabled);
        endpoints.MapCapacityAdminEndpoints(entraEnabled);
        endpoints.MapInferenceBenchmarkAdminEndpoints(entraEnabled);
        endpoints.MapUsageGovernanceEndpoints(entraEnabled);
        endpoints.MapUserRateLimitAdminEndpoints(entraEnabled);
        endpoints.MapUserTokenBudgetAdminEndpoints(entraEnabled);
        endpoints.MapUsageGroupRateLimitAdminEndpoints(entraEnabled);
        endpoints.MapOutputTokenBudgetAdminEndpoints(entraEnabled);
        endpoints.MapGovernanceCredentialEndpoints(entraEnabled);
        endpoints.MapDataRetentionAdminEndpoints(entraEnabled);
        endpoints.MapProductReleaseAdminEndpoints(entraEnabled);
        endpoints.MapProductUpdateAdminEndpoints(entraEnabled);
        endpoints.MapAdminOperationEndpoints(entraEnabled);
        return endpoints;
    }
}
