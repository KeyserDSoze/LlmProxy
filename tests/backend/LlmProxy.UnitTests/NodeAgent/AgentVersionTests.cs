using System.Reflection;
using System.Reflection.Emit;
using LlmProxy.NodeAgent;

namespace LlmProxy.UnitTests.NodeAgent;

public sealed class AgentVersionTests
{
    [Fact]
    public void Release_informational_version_is_reported_even_if_assembly_version_is_pinned()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("VersionedAgentExample") { Version = new Version(0, 2, 0, 0) },
            AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
            ["0.2.46+e8b1c2d"]));
        Assert.Equal("0.2.46", NodeAgentVersion.Resolve(assembly));
    }

    [Fact]
    public void Public_agent_version_uses_informational_semver()
    {
        var version = NodeAgentVersion.Current;
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.NotEqual("0.0.0", version);
        Assert.DoesNotContain('+', version);
    }
}
