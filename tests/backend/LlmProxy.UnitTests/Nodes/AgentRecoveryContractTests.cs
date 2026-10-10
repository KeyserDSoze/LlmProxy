using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using LlmProxy.NodeAgent;
using Microsoft.Extensions.Configuration;

namespace LlmProxy.UnitTests.Nodes;

public sealed class AgentRecoveryContractTests
{
    [Fact]
    public void Recovery_options_load_server_specific_identity_and_secret()
    {
        var nodeId = Guid.NewGuid();
        const string token = "lpr_a_random_example_secret_that_is_not_real";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["NodeAgent:GatewayBaseAddress"] = "https://example.test",
                ["NodeAgent:RecoveryNodeId"] = nodeId.ToString(),
                ["NodeAgent:RecoveryToken"] = token,
                ["NodeAgent:ForceRecovery"] = "true",
                ["NodeAgent:DataDirectory"] = "/var/lib/llmproxy-node-agent"
            }).Build();

        var options = NodeAgentOptions.From(configuration);
        Assert.Equal(nodeId, options.RecoveryNodeId);
        Assert.Equal(token, options.RecoveryToken);
        Assert.True(options.ForceRecovery);
        Assert.Equal("/var/lib/llmproxy-node-agent", options.DataDirectory);
    }

    [Fact]
    public void Ordinary_reboot_does_not_request_recovery()
    {
        var options = NodeAgentOptions.From(new ConfigurationBuilder().Build());
        Assert.False(options.ForceRecovery);
        Assert.Null(options.RecoveryNodeId);
        Assert.Null(options.RecoveryToken);
    }

    [Fact]
    public void Recovery_key_is_encrypted_and_distinct_from_operational_agent_bearer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> {
                ["Security:UpstreamCredentialEncryptionKey"] =
                    "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
            }).Build();
        var protector = new UpstreamCredentialProtector(config);
        const string recovery = "lpr_per_server_example_recovery";
        const string agent = "lpa_ephemeral_operational_identity";
        var enrollment = new NodeEnrollmentRecord
        {
            NodeId = Guid.NewGuid(),
            AgentSecretHash = "digest_of_" + agent,
            RecoverySecretCiphertext = protector.Protect(recovery),
            RecoverySecretCreatedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.DoesNotContain(recovery, enrollment.RecoverySecretCiphertext, StringComparison.Ordinal);
        Assert.Equal(recovery, protector.Unprotect(enrollment.RecoverySecretCiphertext!));
        Assert.NotEqual(agent, protector.Unprotect(enrollment.RecoverySecretCiphertext!));
    }
}
