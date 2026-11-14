using LlmProxy.Infrastructure.Persistence;

namespace LlmProxy.Api.Admin;

/// <summary>When an engine's automatic update is due. Pure, so the daily schedule can be tested without a clock or a database.</summary>
public static class RuntimeEngineSchedule
{
    /// <summary>A missed slot (gateway restart, Agent unreachable) is still tried for this long, then skipped until tomorrow.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);

    /// <summary>Minimum gap between two attempts at the same slot, so a busy or unreachable Agent is retried without hammering.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    public static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        if (value is not { Length: 5 } || value[2] != ':' || !char.IsAsciiDigit(value[0]) || !char.IsAsciiDigit(value[1]) ||
            !char.IsAsciiDigit(value[3]) || !char.IsAsciiDigit(value[4])) return false;
        var hours = (value[0] - '0') * 10 + (value[1] - '0');
        var minutes = (value[3] - '0') * 10 + (value[4] - '0');
        if (hours > 23 || minutes > 59) return false;
        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    public static TimeZoneInfo? ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    /// <summary>The most recent moment (not after <paramref name="nowUtc"/>) the policy was meant to run, or null if none within a day.</summary>
    public static DateTimeOffset? LatestSlotUtc(string updateAtLocal, string timeZone, DateTimeOffset nowUtc)
    {
        if (!TryParseTime(updateAtLocal, out var time)) return null;
        var zone = ResolveZone(timeZone) ?? TimeZoneInfo.Utc;
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        DateTimeOffset? latest = null;
        foreach (var dayOffset in new[] { 0, -1 })
        {
            var local = localNow.Date.AddDays(dayOffset) + time;
            // A wall-clock time skipped by the spring clock change runs one hour later.
            if (zone.IsInvalidTime(local)) local = local.AddHours(1);
            var utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
            if (utc <= nowUtc && (latest is null || utc > latest)) latest = utc;
        }
        return latest;
    }

    public static bool IsDue(RuntimeEnginePolicyRecord policy, DateTimeOffset nowUtc)
    {
        if (!policy.AutoUpdate || LatestSlotUtc(policy.UpdateAtLocal, policy.TimeZone, nowUtc) is not { } slot) return false;
        if (nowUtc - slot >= CatchUpWindow) return false;
        if (policy.LastRunAtUtc is { } ran && ran >= slot) return false;
        return policy.LastAttemptAtUtc is not { } attempt || nowUtc - attempt >= RetryInterval;
    }
}
