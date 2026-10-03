namespace LlmProxy.Infrastructure.Persistence;

public sealed class UserAccessSettingsRecord
{
    public const int SingletonId = 1;
    public const string AutomaticMode = "automatic";
    public const string ManualMode = "manual";

    public int Id { get; set; } = SingletonId;
    public string ProvisioningMode { get; set; } = ManualMode;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
