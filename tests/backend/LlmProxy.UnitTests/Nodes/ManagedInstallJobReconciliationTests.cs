using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Nodes;

public sealed class ManagedInstallJobReconciliationTests
{
    private static readonly NodeModelManagementEndpoints.AgentInstallRequest Request =
        new("whisper-tiny-cpu", "Systran/faster-whisper-tiny",
            null, 1, [], "faster-whisper", 1, null, null, null);

    private static readonly NodeModelManagementEndpoints.ManagedModelState State =
        new("whisper-1", "whisper-tiny-cpu", "Systran/faster-whisper-tiny",
            "stopped", null, Runtime: "faster-whisper");

    private static NodeModelManagementEndpoints.ModelInstallJob Job(
        string status = "completed",
        NodeModelManagementEndpoints.ManagedModelState? result = null) =>
        new(Guid.NewGuid(), Request, status, "ready", 100,
            "Completed", null, null, result ?? State, null, DateTimeOffset.UtcNow);

    [Fact]
    public void Completed_job_is_finalizable_only_while_matching_installation_still_exists()
    {
        Assert.True(NodeModelManagementEndpoints.CanReconcileJob(Job(), [State]));
        Assert.False(NodeModelManagementEndpoints.CanReconcileJob(Job("running"), [State]));
        Assert.False(NodeModelManagementEndpoints.CanReconcileJob(Job(), []));
        Assert.False(NodeModelManagementEndpoints.CanReconcileJob(Job(), [
            State with { InstallationId = "different" }]));
        Assert.False(NodeModelManagementEndpoints.CanReconcileJob(Job(), [
            State with { ProviderModelName = "different/model" }]));
        Assert.False(NodeModelManagementEndpoints.CanReconcileJob(Job(), [
            State with { Runtime = "vllm" }]));
        Assert.False(NodeModelManagementEndpoints.CanReconcileJob(Job(), [
            State with { Status = "error" }]));
    }

    [Fact]
    public void Opted_in_autostart_requires_a_matching_live_runtime_address()
    {
        var job = Job() with {
            Request = Request with { StartAfterInstall = true },
            Result = State with { Status = "running", RuntimeBaseAddress = "http://agent:18001", Port = 18001 }
        };
        var verified = State with { Status = "running", RuntimeBaseAddress = "http://agent:18001", Port = 18001 };
        Assert.Equal("running", NodeModelManagementEndpoints.ResolveVerifiedCompletedState(job, [verified]).Status);
        Assert.Equal("http://agent:18001",
            NodeModelManagementEndpoints.ResolveVerifiedCompletedState(job, [verified]).RuntimeBaseAddress);

        var missing = NodeModelManagementEndpoints.ResolveVerifiedCompletedState(job, []);
        Assert.Equal("stopped", missing.Status);
        Assert.Null(missing.RuntimeBaseAddress);

        var mismatched = NodeModelManagementEndpoints.ResolveVerifiedCompletedState(job, [
            verified with { ProviderModelName = "attacker/other" }
        ]);
        Assert.Equal("stopped", mismatched.Status);

        var stopped = NodeModelManagementEndpoints.ResolveVerifiedCompletedState(job, [
            verified with { Status = "stopped" }
        ]);
        Assert.Equal("stopped", stopped.Status);
    }

    [Fact]
    public void Download_only_jobs_never_enable_routing_automatically()
    {
        var state = State with { Status = "running", RuntimeBaseAddress = "http://agent:18001", Port = 18001 };
        var result = NodeModelManagementEndpoints.ResolveVerifiedCompletedState(Job(), [state]);
        Assert.Equal("stopped", result.Status);
        Assert.Null(result.RuntimeBaseAddress);
    }

    [Fact]
    public void Unknown_custom_models_cannot_be_automatically_published()
    {
        var customRequest = Request with { CatalogModelId = "custom" };
        var job = Job() with { Request = customRequest };
        Assert.False(NodeModelManagementEndpoints.CanReconcileJob(job, [State]));
    }
}
