using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ManagedRuntimeActivationTests
{
    [Theory]
    [InlineData("running", "http://spark-host:18000", 18000, true)]
    [InlineData("Running", "http://spark-host:18000", 18000, true)]
    [InlineData("stopped", "http://spark-host:18000", 18000, false)]
    [InlineData("error", "http://spark-host:18000", 18000, false)]
    [InlineData("running", null, 18000, false)]
    [InlineData("running", "http://spark-host:18000", null, false)]
    [InlineData("running", "http://spark-host:18000", 80, false)]
    [InlineData("running", "http://spark-host:18000", 65536, false)]
    public void Only_running_registered_runtimes_can_be_probed_for_activation(
        string status, string? address, int? port, bool expected)
    {
        var state = new NodeModelManagementEndpoints.ManagedModelState(
            "qwen3-8b-airllm-test", "qwen3-8b-airllm", "Qwen/Qwen3-8B",
            status, address, port, Runtime: "airllm");

        Assert.Equal(expected, NodeModelManagementEndpoints.IsAgentRuntimeRunnable(state));
    }
}
