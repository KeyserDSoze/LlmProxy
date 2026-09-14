using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;

namespace LlmProxy.Infrastructure.Runtime;

public sealed class NullRuntimeStateEventSink : IRuntimeStateEventSink
{
    private static readonly RuntimeStateSyncStatus Status = new(
        false,
        "local-only",
        Environment.MachineName,
        false,
        0,
        0,
        0,
        null);

    public RuntimeStateSyncStatus GetStatus() => Status;

    public void PublishRouteCatalogSnapshot(IEnumerable<RouteNodeSnapshot> nodes, IEnumerable<RouteModelSnapshot> models, IEnumerable<RouteDeploymentSnapshot> deployments) { }
    public void PublishNodeUpsert(RouteNodeSnapshot node) { }
    public void PublishNodeRemove(Guid nodeId) { }
    public void PublishModelUpsert(RouteModelSnapshot model) { }
    public void PublishModelRemove(Guid modelId) { }
    public void PublishDeploymentUpsert(RouteDeploymentSnapshot deployment) { }
    public void PublishDeploymentRemove(Guid deploymentId) { }
    public void PublishCredentialSnapshot(IEnumerable<ApiCredentialSnapshot> credentials) { }
    public void PublishCredentialUpsert(ApiCredentialSnapshot credential) { }
    public void PublishCredentialRemove(Guid credentialId) { }
    public void PublishRatePolicySnapshot(IEnumerable<RateLimitPolicySnapshot> policies) { }
    public void PublishRatePolicyUpsert(RateLimitPolicySnapshot policy) { }
    public void PublishRatePolicyRemove(Guid policyId) { }
}
