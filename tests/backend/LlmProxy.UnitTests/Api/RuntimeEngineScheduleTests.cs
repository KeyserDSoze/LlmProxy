using LlmProxy.Api.Admin;
using LlmProxy.Infrastructure.Persistence;

namespace LlmProxy.UnitTests.Api;

public sealed class RuntimeEngineScheduleTests
{
    private static RuntimeEnginePolicyRecord Policy(string time = "03:30", string zone = "Europe/Rome", bool auto = true) =>
        new() { NodeId = Guid.NewGuid(), EngineId = "llama.cpp", AutoUpdate = auto, UpdateAtLocal = time, TimeZone = zone };

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("03:30", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("3:30", false)]
    [InlineData("03:60", false)]
    [InlineData("ab:cd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Time_must_be_hh_mm(string? value, bool valid) => Assert.Equal(valid, RuntimeEngineSchedule.TryParseTime(value, out _));

    [Theory]
    [InlineData("llama.cpp", true)]
    [InlineData("faster-whisper", true)]
    [InlineData("vllm", true)]
    [InlineData("", false)]
    [InlineData("../etc", false)]
    [InlineData("LLAMA", false)]
    [InlineData("a b", false)]
    public void Engine_ids_are_short_lowercase_names(string id, bool valid) => Assert.Equal(valid, RuntimeEngineAdminEndpoints.ValidEngineId(id));

    [Fact]
    public void Time_zone_must_be_a_known_iana_zone()
    {
        Assert.NotNull(RuntimeEngineSchedule.ResolveZone("Europe/Rome"));
        Assert.Null(RuntimeEngineSchedule.ResolveZone("Mars/Olympus"));
        Assert.Null(RuntimeEngineSchedule.ResolveZone(""));
    }

    [Fact]
    public void An_update_is_due_once_the_local_time_has_passed_and_not_before()
    {
        // 03:30 in Rome in November (UTC+1) is 02:30 UTC.
        Assert.False(RuntimeEngineSchedule.IsDue(Policy(), Utc(11, 14, 2, 29)));
        Assert.True(RuntimeEngineSchedule.IsDue(Policy(), Utc(11, 14, 2, 30)));
        Assert.True(RuntimeEngineSchedule.IsDue(Policy(), Utc(11, 14, 9, 0)));
    }

    [Fact]
    public void The_schedule_follows_daylight_saving()
    {
        // In July Rome is UTC+2, so 03:30 local is 01:30 UTC.
        Assert.False(RuntimeEngineSchedule.IsDue(Policy(), Utc(7, 14, 1, 29)));
        Assert.True(RuntimeEngineSchedule.IsDue(Policy(), Utc(7, 14, 1, 30)));
    }

    [Fact]
    public void A_slot_that_already_ran_is_not_repeated_but_tomorrows_is_due()
    {
        var policy = Policy();
        policy.LastRunAtUtc = Utc(11, 14, 2, 31);

        Assert.False(RuntimeEngineSchedule.IsDue(policy, Utc(11, 14, 8, 0)));
        Assert.True(RuntimeEngineSchedule.IsDue(policy, Utc(11, 15, 2, 30)));
    }

    [Fact]
    public void A_missed_slot_is_caught_up_for_twelve_hours_then_left_for_the_next_day()
    {
        Assert.True(RuntimeEngineSchedule.IsDue(Policy(), Utc(11, 14, 14, 29)));
        Assert.False(RuntimeEngineSchedule.IsDue(Policy(), Utc(11, 14, 14, 30)));
    }

    [Fact]
    public void A_slot_after_midnight_is_still_found_late_in_the_evening_of_the_previous_utc_day()
    {
        // 23:50 local (Rome, UTC+1) = 22:50 UTC; at 23:10 UTC the same slot is 20 minutes old.
        Assert.True(RuntimeEngineSchedule.IsDue(Policy("23:50"), Utc(11, 14, 23, 10)));
    }

    [Fact]
    public void A_busy_agent_is_retried_only_after_the_retry_interval()
    {
        var policy = Policy();
        policy.LastAttemptAtUtc = Utc(11, 14, 2, 40);

        Assert.False(RuntimeEngineSchedule.IsDue(policy, Utc(11, 14, 2, 54)));
        Assert.True(RuntimeEngineSchedule.IsDue(policy, Utc(11, 14, 2, 55)));
    }

    [Fact]
    public void A_disabled_policy_or_unreadable_time_is_never_due()
    {
        Assert.False(RuntimeEngineSchedule.IsDue(Policy(auto: false), Utc(11, 14, 9, 0)));
        Assert.False(RuntimeEngineSchedule.IsDue(Policy("bad"), Utc(11, 14, 9, 0)));
    }

    [Fact]
    public void An_unknown_time_zone_falls_back_to_utc_instead_of_never_running()
    {
        Assert.True(RuntimeEngineSchedule.IsDue(Policy("03:30", "Mars/Olympus"), Utc(11, 14, 3, 30)));
        Assert.False(RuntimeEngineSchedule.IsDue(Policy("03:30", "Mars/Olympus"), Utc(11, 14, 3, 29)));
    }

    [Fact]
    public void A_wall_clock_time_skipped_by_the_spring_change_runs_an_hour_later()
    {
        // Rome moves 02:00 -> 03:00 on 2026-03-29; 02:30 does not exist and runs at 03:30 local (01:30 UTC).
        var policy = Policy("02:30");

        Assert.False(RuntimeEngineSchedule.IsDue(policy, Utc(3, 29, 1, 29)));
        Assert.True(RuntimeEngineSchedule.IsDue(policy, Utc(3, 29, 1, 30)));
    }
}
