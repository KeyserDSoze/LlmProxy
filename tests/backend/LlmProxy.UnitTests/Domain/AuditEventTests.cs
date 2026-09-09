using LlmProxy.Domain.Audit;

namespace LlmProxy.UnitTests.Domain;

public sealed class AuditEventTests
{
    [Fact]
    public void Constructor_requires_actor_action_and_entity_identity()
    {
        Assert.Throws<ArgumentException>(() => new AuditEvent("", "node.update", "node", "123"));
        Assert.Throws<ArgumentException>(() => new AuditEvent("admin", "", "node", "123"));
        Assert.Throws<ArgumentException>(() => new AuditEvent("admin", "node.update", "", "123"));
        Assert.Throws<ArgumentException>(() => new AuditEvent("admin", "node.update", "node", ""));
    }

    [Fact]
    public void Constructor_truncates_untrusted_optional_values_to_persistence_limits()
    {
        var audit = new AuditEvent(
            "admin",
            "node.update",
            "node",
            "123",
            new string('1', 100),
            new string('x', 5000));

        Assert.Equal(64, audit.SourceIp!.Length);
        Assert.Equal(4000, audit.DetailsJson!.Length);
    }
}
