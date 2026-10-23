using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.UnitTests.Nodes;

public sealed class NodeConnectionTestTargetTests
{
    [Fact]
    public void Stopped_managed_deployments_cannot_be_probed_through_stale_node_runtime()
    {
        var node = new InferenceNode("spark", "http://agent-abc.llmproxy.invalid/runtime/18000");
        node.SetManagementBaseAddress("http://agent-abc.llmproxy.invalid/management");
        Assert.Equal("http://agent-abc.llmproxy.invalid/management",
            LlmProxy.Api.Admin.AdminEndpoints.ChooseNodeTestRoot(node, []));
    }

    [Fact]
    public void Enabled_managed_runtime_wins_over_old_physical_node_root()
    {
        var node = new InferenceNode("spark", "http://agent-abc.llmproxy.invalid/runtime/18000");
        node.SetManagementBaseAddress("http://agent-abc.llmproxy.invalid/management");
        Assert.Equal("http://agent-abc.llmproxy.invalid/runtime/18005",
            LlmProxy.Api.Admin.AdminEndpoints.ChooseNodeTestRoot(node,
                ["http://agent-abc.llmproxy.invalid/runtime/18005"]));
    }
}
