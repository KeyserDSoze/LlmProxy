namespace LlmProxy.Infrastructure.Persistence;

public sealed class PlatformUserRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string? PrincipalName { get; set; }
    public string? DisplayName { get; set; }
    public Guid? UsageGroupId { get; set; }
    public bool Enabled { get; set; } = true;
    public string ProvisioningSource { get; set; } = "admin";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public DateTimeOffset? DisabledAtUtc { get; set; }
}
