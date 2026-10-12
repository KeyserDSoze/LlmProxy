namespace LlmProxy.Infrastructure.Persistence;

/// <summary>Single database-backed choice shared by all Gateway replicas.</summary>
public sealed class SystemOneDefaultRecord
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;
    public string? PublicModelName { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
