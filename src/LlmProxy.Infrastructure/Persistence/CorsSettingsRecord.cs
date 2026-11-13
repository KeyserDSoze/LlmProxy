namespace LlmProxy.Infrastructure.Persistence;

/// <summary>Site-wide browser (CORS) settings; one row, absent row means "no origins allowed".</summary>
public sealed class CorsSettingsRecord
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string? AllowedOriginsText { get; set; }
    public int MaxAgeSeconds { get; set; } = 600;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
