namespace LlmProxy.Infrastructure.Persistence;

public sealed class ProductUpdatePolicyRecord
{
    public const int SingletonId = 1;
    public const string ManualMode = "manual";
    public const string AsapMode = "asap";
    public const string NightlyMode = "nightly";
    public const string WeeklyMode = "weekly";
    public const string MonthlyMode = "monthly";

    public int Id { get; set; } = SingletonId;
    public string Mode { get; set; } = ManualMode;
    public string TimeZoneId { get; set; } = "UTC";
    public int LocalHour { get; set; } = 2;
    public int LocalMinute { get; set; }
    public int DayOfWeek { get; set; }
    public int DayOfMonth { get; set; } = 1;
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public DateTimeOffset? LastScheduledAtUtc { get; set; }
    public string? LastScheduledVersion { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public static bool IsSupportedMode(string? mode) =>
        mode is ManualMode or AsapMode or NightlyMode or WeeklyMode or MonthlyMode;
}
