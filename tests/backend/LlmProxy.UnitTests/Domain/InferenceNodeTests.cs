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
    public void Hardware_metrics_address_is_optional_and_normalized_as_service_root()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");

        node.SetHardwareMetricsBaseAddress("http://10.0.0.21:9400/dcgm/");

        Assert.Equal("http://10.0.0.21:9400/dcgm", node.HardwareMetricsBaseAddress);
        node.SetHardwareMetricsBaseAddress(null);
        Assert.Null(node.HardwareMetricsBaseAddress);
    }

    [Fact]
    public void Hardware_metrics_address_rejects_non_http_uri()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");

        Assert.Throws<ArgumentException>(() => node.SetHardwareMetricsBaseAddress("not-a-uri"));
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
    public void Node_requires_consecutive_successes_before_becoming_healthy()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");
        var now = DateTimeOffset.UtcNow;

        node.RecordHealthSuccess(now, 12, healthyAfterSuccesses: 2);
        Assert.Equal(NodeStatus.Degraded, node.Status);
        Assert.Equal(1, node.ConsecutiveHealthSuccesses);

        node.RecordHealthSuccess(now.AddSeconds(1), 10, healthyAfterSuccesses: 2);
        Assert.Equal(NodeStatus.Healthy, node.Status);
        Assert.Equal(2, node.ConsecutiveHealthSuccesses);
        Assert.Equal(0, node.ConsecutiveHealthFailures);
        Assert.Equal(10, node.LastHealthLatencyMilliseconds);
        Assert.Null(node.LastHealthError);
        Assert.Equal(now.AddSeconds(1), node.LastHealthyAtUtc);
    }

    [Fact]
    public void Node_degrades_before_becoming_unhealthy_after_repeated_failures()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");
        var now = DateTimeOffset.UtcNow;
        node.RecordHealthSuccess(now, 10, healthyAfterSuccesses: 1);

        node.RecordHealthFailure(now.AddSeconds(1), 30, "HTTP 500", unhealthyAfterFailures: 3);
        Assert.Equal(NodeStatus.Degraded, node.Status);
        Assert.Equal(1, node.ConsecutiveHealthFailures);

        node.RecordHealthFailure(now.AddSeconds(2), 31, "HTTP 500", unhealthyAfterFailures: 3);
        Assert.Equal(NodeStatus.Degraded, node.Status);

        node.RecordHealthFailure(now.AddSeconds(3), 32, "HTTP 500", unhealthyAfterFailures: 3);
        Assert.Equal(NodeStatus.Unhealthy, node.Status);
        Assert.Equal(3, node.ConsecutiveHealthFailures);
        Assert.Equal("HTTP 500", node.LastHealthError);
        Assert.Equal(32, node.LastHealthLatencyMilliseconds);
    }

    [Fact]
    public void Unhealthy_node_requires_success_streak_to_recover()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");
        var now = DateTimeOffset.UtcNow;
        node.RecordHealthFailure(now, 5, "down", unhealthyAfterFailures: 1);
        Assert.Equal(NodeStatus.Unhealthy, node.Status);

        node.RecordHealthSuccess(now.AddSeconds(1), 8, healthyAfterSuccesses: 2);
        Assert.Equal(NodeStatus.Degraded, node.Status);

        node.RecordHealthSuccess(now.AddSeconds(2), 7, healthyAfterSuccesses: 2);
        Assert.Equal(NodeStatus.Healthy, node.Status);
        Assert.Equal(2, node.ConsecutiveHealthSuccesses);
        Assert.Equal(0, node.ConsecutiveHealthFailures);
    }

    [Fact]
    public void Health_result_does_not_override_draining_state()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");
        node.StartDrain();

        node.RecordHealthSuccess(DateTimeOffset.UtcNow, 4);
        node.RecordHealthFailure(DateTimeOffset.UtcNow, 4, "ignored");

        Assert.Equal(NodeStatus.Draining, node.Status);
        Assert.Null(node.LastHealthCheckUtc);
    }

    [Fact]
    public void Enabling_disabled_node_resets_health_streaks()
    {
        var node = new InferenceNode("dgx-01", "http://10.0.0.21:8000");
        node.RecordHealthFailure(DateTimeOffset.UtcNow, 10, "down", unhealthyAfterFailures: 1);
        node.Disable();

        node.Enable();

        Assert.True(node.Enabled);
        Assert.Equal(NodeStatus.Unknown, node.Status);
        Assert.Equal(0, node.ConsecutiveHealthFailures);
        Assert.Equal(0, node.ConsecutiveHealthSuccesses);
        Assert.Null(node.LastHealthError);
    }
}
