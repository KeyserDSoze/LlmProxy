using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class ManagedModelBootstrapTests
{
    private static ManagedModelRecord Record(string status, bool? desired = null, string id = "model-a") => new()
    {
        InstallationId = id, CatalogModelId = "c", ProviderModelName = "p/m", Port = 18000, TensorParallelSize = 1, Status = status, DesiredRunning = desired
    };

    [Theory]
    [InlineData("running", null, true)]    // an older Agent's record that was running before the reboot
    [InlineData("stopped", null, false)]
    [InlineData("error", null, false)]
    [InlineData("running", false, false)]  // the operator stopped it
    [InlineData("error", true, true)]      // it was wanted and the last start failed: still wanted
    [InlineData("stopped", true, true)]
    public void A_model_is_wanted_when_the_operator_last_asked_for_it_running(string status, bool? desired, bool wanted) =>
        Assert.Equal(wanted, ManagedModelBootstrap.IsWanted(Record(status, desired)));

    [Theory]
    [InlineData("stopped", true)]
    [InlineData("error", true)]
    [InlineData("running", false)]
    [InlineData("installing", false)]
    public void Only_a_wanted_model_that_is_not_up_needs_a_start(string live, bool needs) =>
        Assert.Equal(needs, ManagedModelBootstrap.NeedsStart(Record("running", true), live));

    [Fact]
    public void A_stopped_model_is_never_started_by_the_agent() =>
        Assert.False(ManagedModelBootstrap.NeedsStart(Record("stopped", false), "stopped"));

    [Fact]
    public void Pauses_grow_with_each_failure_and_stay_at_the_last_step()
    {
        var delays = Enumerable.Range(1, 8).Select(failed => ManagedModelBootstrap.DelayAfterFailure(failed).TotalMinutes).ToArray();

        Assert.Equal([1, 2, 5, 10, 15, 15, 15, 15], delays);
        Assert.Equal(1, ManagedModelBootstrap.DelayAfterFailure(0).TotalMinutes);
    }

    [Fact]
    public void The_model_card_says_what_the_agent_is_doing()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(ManagedModelBootstrap.Describe(null, now));
        Assert.Equal("waiting", ManagedModelBootstrap.Describe(new BootstrapAttempt { Reason = "GPU too hot" }, now)!.Value.Status);
        var starting = ManagedModelBootstrap.Describe(new BootstrapAttempt { Attempts = 2, JobId = Guid.NewGuid() }, now)!.Value;
        Assert.Equal("starting", starting.Status);
        Assert.Contains("attempt 2 of 5", starting.Detail);
        var retry = ManagedModelBootstrap.Describe(new BootstrapAttempt { Attempts = 1, NextAtUtc = now.AddMinutes(4.5), Reason = "out of memory" }, now)!.Value;
        Assert.Equal("retrying", retry.Status);
        Assert.Contains("5 min", retry.Detail);
        Assert.Contains("out of memory", retry.Detail);
        var gaveUp = ManagedModelBootstrap.Describe(new BootstrapAttempt { Attempts = 5, GaveUp = true, Reason = "boom" }, now)!.Value;
        Assert.Equal("gave_up", gaveUp.Status);
        Assert.Contains("by hand", gaveUp.Detail);
    }

    [Fact]
    public void The_model_list_carries_the_automatic_restart_status_only_for_models_that_are_not_running()
    {
        var tracker = new BootstrapTracker();
        tracker.Get("down").Reason = "Waiting for the server to be ready.";
        tracker.Get("up").Attempts = 1;
        ManagedModelState State(string id, string status) => new(id, "c", "p/m", status, null, 18000, null);

        var annotated = tracker.Annotate(new ManagedModelsResponse([State("down", "stopped"), State("up", "running"), State("other", "stopped")])).Models;

        Assert.Equal("waiting", annotated.Single(model => model.InstallationId == "down").AutoStart);
        Assert.Null(annotated.Single(model => model.InstallationId == "up").AutoStart);
        Assert.Null(annotated.Single(model => model.InstallationId == "other").AutoStart);
    }

    [Theory]
    [InlineData("vllm/vllm-openai:v5df63a7203cdb9328d824a27b682e77c7b9928be", "v5df63a7203cd")]
    [InlineData("5df63a7203cdb9328d824a27b682e77c7b9928be", "5df63a7203cd")]
    [InlineData("b4567", "b4567")]
    [InlineData("0.11.0", "0.11.0")]
    [InlineData("ghcr.io/ggml-org/llama.cpp:server-cuda", "server-cuda")]
    public void The_version_shown_is_the_part_a_person_can_read(string label, string shown) =>
        Assert.Equal(shown, RuntimeEngineParsing.Readable(label));

    [Fact]
    public void A_very_long_version_is_capped() =>
        Assert.Equal(28, RuntimeEngineParsing.Readable(new string('x', 80)).Length);
}
