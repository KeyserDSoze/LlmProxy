namespace LlmProxy.Infrastructure.Persistence;

/// <summary>One-time pairing invitation and the long-lived per-agent credential digest. No plaintext credentials are stored.</summary>
public sealed class NodeEnrollmentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string InvitationHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
    public Guid? NodeId { get; set; }
    public string? AgentSecretHash { get; set; }
    public string Mode { get; set; } = "outbound";
    public string? HardwareInventoryJson { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public string? AgentVersion { get; set; }
    public string? DesiredAgentVersion { get; set; }
    public string? AgentUpdateStatus { get; set; }
}
