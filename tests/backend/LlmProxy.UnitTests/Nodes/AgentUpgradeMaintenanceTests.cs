using LlmProxy.Api.Admin;

namespace LlmProxy.UnitTests.Nodes;

public sealed class AgentUpgradeMaintenanceTests
{
    [Fact]
    public void Upgrade_missing_contact_alerts_after_elapsed_threshold_only()
    {
        var now = DateTimeOffset.Parse("2026-10-25T01:00:00Z");
        Assert.False(NodeAgentUpgradeMaintenanceWorker.IsUpgradeContactStalled(
            now.AddMinutes(-5), now.AddMinutes(-4), now));
        Assert.False(NodeAgentUpgradeMaintenanceWorker.IsUpgradeContactStalled(
            now.AddMinutes(-15), now.AddMinutes(-1), now));
        Assert.True(NodeAgentUpgradeMaintenanceWorker.IsUpgradeContactStalled(
            now.AddMinutes(-15), now.AddMinutes(-4), now));
        Assert.True(NodeAgentUpgradeMaintenanceWorker.IsUpgradeContactStalled(
            now.AddMinutes(-15), null, now));
    }

    [Fact]
    public void Update_does_not_resume_when_only_the_requested_version_matches()
    {
        Assert.False(NodeAgentUpgradeMaintenanceWorker.IsVerifiedUpgrade(
            "0.2.60", "running:0.2.60:healthcheck", "0.2.60"));
    }

    [Fact]
    public void Update_does_not_resume_on_stale_success_from_wrong_binary()
    {
        Assert.False(NodeAgentUpgradeMaintenanceWorker.IsVerifiedUpgrade(
            "0.2.59", "succeeded:0.2.60:completed:100", "0.2.60"));
    }

    [Fact]
    public void Update_resumes_only_after_matching_version_and_verified_success()
    {
        Assert.True(NodeAgentUpgradeMaintenanceWorker.IsVerifiedUpgrade(
            "0.2.60", "succeeded:0.2.60:completed:100", "0.2.60"));
    }

    [Fact]
    public void Failed_update_is_eligible_for_healthy_rollback_recovery()
    {
        Assert.True(NodeAgentUpgradeMaintenanceWorker.IsFailedUpgrade(
            "failed:0.2.60:healthcheck:health_version_mismatch", "0.2.60"));
        Assert.False(NodeAgentUpgradeMaintenanceWorker.IsFailedUpgrade(
            "failed:0.2.59:healthcheck", "0.2.60"));
    }
}
