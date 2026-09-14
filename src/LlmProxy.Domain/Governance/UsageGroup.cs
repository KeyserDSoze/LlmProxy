namespace LlmProxy.Domain.Governance;

public sealed class UsageGroup
{
    private UsageGroup()
    {
    }

    public UsageGroup(string name, string? description = null)
    {
        Update(name, description);
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void Update(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Usage group name is required.", nameof(name));
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length > 160)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Usage group name cannot exceed 160 characters.");
        }

        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalizedDescription?.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(description), "Usage group description cannot exceed 1000 characters.");
        }

        Name = normalizedName;
        Description = normalizedDescription;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
