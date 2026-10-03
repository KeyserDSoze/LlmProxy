using LlmProxy.UpdateAgent;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmProxy.UnitTests.UpdateAgent;

public sealed class UpdateSchedulerTests
{
    [Fact]
    public async Task Scheduling_preserves_upgrade_path_and_force_replaces_only_pending_work()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"llmproxy-update-agent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var options = new UpdateAgentOptions(
                BearerToken: "test-token",
                InstallDirectory: stateDirectory,
                BootstrapPath: Path.Combine(stateDirectory, "bootstrap.sh"),
                StateDirectory: stateDirectory,
                CommandTimeoutMinutes: 5);
            using var scheduler = new UpdateScheduler(options, NullLogger<UpdateScheduler>.Instance);
            var scheduledFor = DateTimeOffset.UtcNow.AddHours(1);

            var first = await scheduler.ScheduleAsync(
                new ScheduleUpdateRequest(
                    "0.0.9",
                    scheduledFor,
                    Force: false,
                    Versions: ["0.0.7", "0.0.8", "0.0.9"]),
                CancellationToken.None);

            Assert.Equal("Pending", first.Status);
            Assert.Equal(new[] { "0.0.7", "0.0.8", "0.0.9" }, first.UpgradePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scheduler.ScheduleAsync(
                    new ScheduleUpdateRequest("0.0.10", scheduledFor, Force: false),
                    CancellationToken.None));

            var replacement = await scheduler.ScheduleAsync(
                new ScheduleUpdateRequest("0.0.10", scheduledFor, Force: true),
                CancellationToken.None);

            Assert.Equal("0.0.10", replacement.Version);
            Assert.Equal(new[] { "0.0.10" }, replacement.UpgradePath);

            var snapshot = await scheduler.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(replacement.Id, snapshot.ActiveJob?.Id);
            var cancelled = Assert.Single(snapshot.RecentJobs);
            Assert.Equal(first.Id, cancelled.Id);
            Assert.Equal("Cancelled", cancelled.Status);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Scheduling_rejects_an_out_of_order_or_incomplete_upgrade_path()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"llmproxy-update-agent-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var options = new UpdateAgentOptions(
                BearerToken: "test-token",
                InstallDirectory: stateDirectory,
                BootstrapPath: Path.Combine(stateDirectory, "bootstrap.sh"),
                StateDirectory: stateDirectory,
                CommandTimeoutMinutes: 5);
            using var scheduler = new UpdateScheduler(options, NullLogger<UpdateScheduler>.Instance);
            var future = DateTimeOffset.UtcNow.AddHours(1);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                scheduler.ScheduleAsync(
                    new ScheduleUpdateRequest(
                        "0.0.9",
                        future,
                        Force: false,
                        Versions: ["0.0.8", "0.0.7", "0.0.9"]),
                    CancellationToken.None));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                scheduler.ScheduleAsync(
                    new ScheduleUpdateRequest(
                        "0.0.9",
                        future,
                        Force: false,
                        Versions: ["0.0.7", "0.0.8"]),
                    CancellationToken.None));
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }
}
