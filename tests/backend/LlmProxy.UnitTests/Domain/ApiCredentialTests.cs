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
    public void Personal_credential_is_bound_to_stable_entra_identity()
    {
        var credential = new ApiCredential(
            "Project Alpha",
            "lp_abc",
            "HASH",
            ownerTenantId: "tenant-1",
            ownerObjectId: "object-1",
            ownerPrincipalName: "user@example.com");

        Assert.True(credential.IsPersonal);
        Assert.True(credential.IsOwnedBy("TENANT-1", "OBJECT-1"));
        Assert.False(credential.IsOwnedBy("tenant-1", "other-object"));
        Assert.Equal("user@example.com", credential.OwnerPrincipalName);
    }

    [Fact]
    public void Personal_credential_requires_tenant_and_object_id_together()
    {
        Assert.Throws<ArgumentException>(() => new ApiCredential(
            "Invalid",
            "lp_abc",
            "HASH",
            ownerTenantId: "tenant-1"));

        Assert.Throws<ArgumentException>(() => new ApiCredential(
            "Invalid",
            "lp_abc",
            "HASH",
            ownerObjectId: "object-1"));
    }

    [Fact]
    public void Rotation_replaces_prefix_and_hash_without_changing_identity_or_assignment()
    {
        var credential = new ApiCredential(
            "Copilot",
            "lp_old",
            "OLD_HASH",
            DateTimeOffset.UtcNow.AddDays(30),
            "tenant-1",
            "object-1",
            "user@example.com");
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
        Assert.Equal("tenant-1", credential.OwnerTenantId);
        Assert.Equal("object-1", credential.OwnerObjectId);
        Assert.Equal("user@example.com", credential.OwnerPrincipalName);
        Assert.Equal("lp_new", credential.KeyPrefix);
        Assert.Equal("NEW_HASH", credential.KeyHash);
        Assert.True(credential.Enabled);
    }


    [Fact]
    public void Organization_credential_is_caller_governance_exempt_by_default_and_admin_can_opt_in()
    {
        var credential = new ApiCredential("GitHub Copilot", "lp_org", "HASH");

        Assert.False(credential.IsPersonal);
        Assert.False(credential.EnforceCallerGovernance);

        credential.SetCallerGovernance(true);

        Assert.True(credential.EnforceCallerGovernance);
    }

    [Fact]
    public void Personal_credential_is_always_caller_governed()
    {
        var credential = new ApiCredential(
            "Personal",
            "lp_personal",
            "HASH",
            ownerTenantId: "tenant-1",
            ownerObjectId: "object-1");

        Assert.True(credential.EnforceCallerGovernance);
        Assert.Throws<InvalidOperationException>(() => credential.SetCallerGovernance(false));
        Assert.True(credential.EnforceCallerGovernance);
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
