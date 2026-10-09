using LlmProxy.Domain.Deployments;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class ManagedDeploymentIndexTests
{
    [Fact]
    public void Manual_and_managed_deployments_have_separate_uniqueness_rules()
    {
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase("managed-profile-metadata").Options);
        var entity = db.Model.FindEntityType(typeof(ModelDeployment))!;
        var manual = entity.GetIndexes().Single(index => index.Properties.Select(x => x.Name)
            .SequenceEqual(new[] { "NodeId", "ModelId" }));
        Assert.True(manual.IsUnique);
        Assert.Equal("\"ManagedInstallationId\" IS NULL", manual.GetFilter());

        var managed = entity.GetIndexes().Single(index => index.Properties.Select(x => x.Name)
            .SequenceEqual(new[] { "NodeId", "ManagedInstallationId" }));
        Assert.True(managed.IsUnique);
        Assert.Equal("\"ManagedInstallationId\" IS NOT NULL", managed.GetFilter());
    }
}
