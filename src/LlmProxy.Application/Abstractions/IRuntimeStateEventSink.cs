using LlmProxy.Application.Governance;

namespace LlmProxy.Application.Abstractions;

public sealed record RuntimeStateSyncStatus(
    bool Enabled,
    string Provider,
    string InstanceId,
    bool Connected,
    long LastAppliedVersion,
    long PublishedEvents,
    long ReceivedEvents,
    string? LastError);

public interface IRuntimeStateEventSink
{
    RuntimeStateSyncStatus GetStatus();

    void PublishRouteCatalogSnapshot(
        IEnumerable<RouteNodeSnapshot> nodes,
        IEnumerable<RouteModelSnapshot> models,
        IEnumerable<RouteDeploymentSnapshot> deployments);

    void PublishNodeUpsert(RouteNodeSnapshot node);
    void PublishNodeRemove(Guid nodeId);
    void PublishModelUpsert(RouteModelSnapshot model);
    void PublishModelRemove(Guid modelId);
    void PublishDeploymentUpsert(RouteDeploymentSnapshot deployment);
    void PublishDeploymentRemove(Guid deploymentId);

    void PublishCredentialSnapshot(IEnumerable<ApiCredentialSnapshot> credentials);
    void PublishCredentialUpsert(ApiCredentialSnapshot credential);
    void PublishCredentialRemove(Guid credentialId);

    void PublishRatePolicySnapshot(IEnumerable<RateLimitPolicySnapshot> policies);
    void PublishRatePolicyUpsert(RateLimitPolicySnapshot policy);
    void PublishRatePolicyRemove(Guid policyId);
}
