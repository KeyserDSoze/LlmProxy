using LlmProxy.Domain.Security;

namespace LlmProxy.UnitTests.Domain;

public sealed class ApiCredentialTests
{
    [Fact]
    public void Credential_is_usable_before_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var credential = new ApiCredential("Copilot", "lp_abc", "HASH", now.AddMinutes(5));

        Assert.True(credential.IsUsable(now));
    }

    [Fact]
    public void Credential_is_not_usable_after_expiry()
    {
        var now = DateTimeOffset.UtcNow;
        var credential = new ApiCredential("Copilot", "lp_abc", "HASH", now.AddMinutes(-1));

        Assert.False(credential.IsUsable(now));
    }

    [Fact]
    public void Revoked_credential_is_not_usable()
    {
        var credential = new ApiCredential("Copilot", "lp_abc", "HASH");
        credential.Revoke();

        Assert.False(credential.IsUsable(DateTimeOffset.UtcNow));
    }
}
