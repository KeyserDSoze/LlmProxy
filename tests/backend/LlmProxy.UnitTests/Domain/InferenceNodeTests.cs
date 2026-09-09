using LlmProxy.Domain.Nodes;

namespace LlmProxy.UnitTests.Domain;

public sealed class InferenceNodeTests
{
    [Fact]
    public void Constructor_rejects_non_http_address()
    {
        Assert.Throws<ArgumentException>(() => new InferenceNode("dgx-01", "not-a-uri"));
    }

    [Fact]
    public void Constructor_keeps_full_service_root_including_optional_path()
    {
        var node = new InferenceNode("local-primary", "http://localhost:3450/primopath/");

        Assert.Equal("http://localhost:3450/primopath", node.BaseAddress);
    }

    [Fact]
    public void Constructor_accepts_local_ip_address()
    {
        var node = new InferenceNode("dgx-ip", "http://10.0.0.25:8000/vllm");

        Assert.Equal("http://10.0.0.25:8000/vllm", node.BaseAddress);
    }

    [Fact]
    public void Drain_keeps_node_enabled_but_marks_it_draining()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");

        node.StartDrain();

        Assert.True(node.Enabled);
        Assert.Equal(NodeStatus.Draining, node.Status);
    }

    [Fact]
    public void Disabled_node_cannot_be_drained()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");
        node.Disable();

        Assert.Throws<InvalidOperationException>(() => node.StartDrain());
    }

    [Fact]
    public void Health_result_does_not_override_draining_state()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");
        node.StartDrain();

        node.SetHealth(NodeStatus.Healthy, DateTimeOffset.UtcNow);

        Assert.Equal(NodeStatus.Draining, node.Status);
    }
}
