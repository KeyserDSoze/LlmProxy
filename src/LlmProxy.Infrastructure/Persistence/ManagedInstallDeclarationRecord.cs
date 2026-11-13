namespace LlmProxy.Infrastructure.Persistence;

/// <summary>
/// What the operator declared for a model while queueing its download (tools, vision, reasoning, limits).
/// The Agent job does not carry it, so the gateway keeps it until the finished download is registered as a model.
/// </summary>
public sealed class ManagedInstallDeclarationRecord
{
    public Guid JobId { get; set; }
    public Guid NodeId { get; set; }
    public string DeclaredJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
