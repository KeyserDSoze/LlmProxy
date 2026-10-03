using LlmProxy.Api.Product;
using LlmProxy.Infrastructure.Persistence;

namespace LlmProxy.UnitTests.Api;

public sealed class ProductUpdateScheduleTests
{
    [Fact]
    public void Manual_policy_is_never_due()
    {
        var policy = Policy(ProductUpdatePolicyRecord.ManualMode);
        Assert.False(ProductUpdateSchedule.IsDue(policy, DateTimeOffset.Parse("2026-10-03T12:00:00Z")));
    }

    [Fact]
    public void Asap_policy_checks_at_most_every_five_minutes()
    {
        var now = DateTimeOffset.Parse("2026-10-03T12:00:00Z");
        var policy = Policy(ProductUpdatePolicyRecord.AsapMode);

        Assert.True(ProductUpdateSchedule.IsDue(policy, now));

        policy.LastCheckedAtUtc = now.AddMinutes(-4).AddSeconds(-59);
        Assert.False(ProductUpdateSchedule.IsDue(policy, now));

        policy.LastCheckedAtUtc = now.AddMinutes(-5);
        Assert.True(ProductUpdateSchedule.IsDue(policy, now));
    }

    [Fact]
    public void Nightly_policy_runs_once_after_the_next_local_occurrence()
    {
        var policy = Policy(ProductUpdatePolicyRecord.NightlyMode);
        policy.TimeZoneId = "UTC";
        policy.LocalHour = 2;
        policy.LocalMinute = 0;
        policy.LastCheckedAtUtc = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

        Assert.False(ProductUpdateSchedule.IsDue(policy, DateTimeOffset.Parse("2026-10-03T01:59:00Z")));
        Assert.True(ProductUpdateSchedule.IsDue(policy, DateTimeOffset.Parse("2026-10-03T02:00:00Z")));

        policy.LastCheckedAtUtc = DateTimeOffset.Parse("2026-10-03T02:05:00Z");
        Assert.False(ProductUpdateSchedule.IsDue(policy, DateTimeOffset.Parse("2026-10-03T12:00:00Z")));
    }

    [Fact]
    public void Weekly_and_monthly_policies_respect_their_calendar_boundary()
    {
        var weekly = Policy(ProductUpdatePolicyRecord.WeeklyMode);
        weekly.TimeZoneId = "UTC";
        weekly.DayOfWeek = (int)DayOfWeek.Saturday;
        weekly.LocalHour = 3;
        weekly.LastCheckedAtUtc = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

        Assert.False(ProductUpdateSchedule.IsDue(weekly, DateTimeOffset.Parse("2026-10-03T02:59:00Z")));
        Assert.True(ProductUpdateSchedule.IsDue(weekly, DateTimeOffset.Parse("2026-10-03T03:00:00Z")));

        var monthly = Policy(ProductUpdatePolicyRecord.MonthlyMode);
        monthly.TimeZoneId = "UTC";
        monthly.DayOfMonth = 31;
        monthly.LocalHour = 1;
        monthly.LastCheckedAtUtc = DateTimeOffset.Parse("2026-09-01T12:00:00Z");

        Assert.False(ProductUpdateSchedule.IsDue(monthly, DateTimeOffset.Parse("2026-09-30T00:59:00Z")));
        Assert.True(ProductUpdateSchedule.IsDue(monthly, DateTimeOffset.Parse("2026-09-30T01:00:00Z")));
    }

    private static ProductUpdatePolicyRecord Policy(string mode) =>
        new()
        {
            Id = ProductUpdatePolicyRecord.SingletonId,
            Mode = mode,
            TimeZoneId = "UTC",
            LocalHour = 2,
            LocalMinute = 0,
            DayOfWeek = 0,
            DayOfMonth = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
}
