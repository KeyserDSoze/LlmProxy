namespace LlmProxy.Domain.Audit;

public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public AuditEvent(
        string actor,
        string action,
        string entityType,
        string entityId,
        string? sourceIp = null,
        string? detailsJson = null)
    {
        Actor = Required(actor, nameof(actor), 320);
        Action = Required(action, nameof(action), 100);
        EntityType = Required(entityType, nameof(entityType), 100);
        EntityId = Required(entityId, nameof(entityId), 200);
        SourceIp = Optional(sourceIp, 64);
        DetailsJson = Optional(detailsJson, 4000);
    }

    public long Id { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; } = DateTimeOffset.UtcNow;
    public string Actor { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? SourceIp { get; private set; }
    public string? DetailsJson { get; private set; }

    private static string Required(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
