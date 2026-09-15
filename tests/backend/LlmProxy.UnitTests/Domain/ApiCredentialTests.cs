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

    [Fact]
    public void Rotation_replaces_prefix_and_hash_without_changing_identity_or_assignment()
    {
        var credential = new ApiCredential("Copilot", "lp_old", "OLD_HASH", DateTimeOffset.UtcNow.AddDays(30));
        var usageGroupId = Guid.NewGuid();
        credential.AssignUsageGroup(usageGroupId);
        var id = credential.Id;
        var createdAtUtc = credential.CreatedAtUtc;
        var expiresAtUtc = credential.ExpiresAtUtc;

        credential.Rotate("lp_new", "NEW_HASH");

        Assert.Equal(id, credential.Id);
        Assert.Equal(createdAtUtc, credential.CreatedAtUtc);
        Assert.Equal(expiresAtUtc, credential.ExpiresAtUtc);
        Assert.Equal(usageGroupId, credential.UsageGroupId);
        Assert.Equal("lp_new", credential.KeyPrefix);
        Assert.Equal("NEW_HASH", credential.KeyHash);
        Assert.True(credential.Enabled);
    }

    [Fact]
    public void Revoked_credential_cannot_be_rotated()
    {
        var credential = new ApiCredential("Copilot", "lp_old", "OLD_HASH");
        credential.Revoke();

        var exception = Assert.Throws<InvalidOperationException>(() => credential.Rotate("lp_new", "NEW_HASH"));

        Assert.Equal("Revoked credentials cannot be rotated.", exception.Message);
        Assert.Equal("lp_old", credential.KeyPrefix);
        Assert.Equal("OLD_HASH", credential.KeyHash);
    }
}
