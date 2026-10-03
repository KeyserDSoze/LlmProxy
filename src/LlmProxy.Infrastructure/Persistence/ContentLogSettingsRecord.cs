namespace LlmProxy.Infrastructure.Persistence;

public sealed class ContentLogSettingsRecord
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public int RetentionDays { get; set; } = 30;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
