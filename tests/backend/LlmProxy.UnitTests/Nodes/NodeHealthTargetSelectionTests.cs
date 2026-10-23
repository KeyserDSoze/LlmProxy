using LlmProxy.Infrastructure.Health;

namespace LlmProxy.UnitTests.Nodes;

public sealed class NodeHealthTargetSelectionTests
{
    private const string OldRoot = "http://agent-3ddee41b.llmproxy.invalid/runtime/18000";

    [Fact]
    public void Enabled_systemone_classifier_uses_its_explicit_runtime_not_stopped_qwen_port()
    {
        Assert.Equal("http://10.0.0.2:8002",
            NodeHealthMonitor.ChooseDeploymentHealthRoot(
                OldRoot, "http://10.0.0.2:8002", isManaged: false));
    }

    [Fact]
    public void Enabled_managed_runtime_uses_its_assigned_port()
    {
        Assert.Equal("http://agent-3ddee41b.llmproxy.invalid/runtime/18005",
            NodeHealthMonitor.ChooseDeploymentHealthRoot(OldRoot,
                "http://agent-3ddee41b.llmproxy.invalid/runtime/18005", isManaged: true));
    }

    [Fact]
    public void Managed_runtime_without_serving_address_must_not_probe_old_node_root()
    {
        Assert.Null(NodeHealthMonitor.ChooseDeploymentHealthRoot(
            OldRoot, null, isManaged: true));
    }

    [Fact]
    public void Legacy_unmanaged_deployment_without_override_preserves_node_default()
    {
        Assert.Equal(OldRoot, NodeHealthMonitor.ChooseDeploymentHealthRoot(
            OldRoot, null, isManaged: false));
    }
}
