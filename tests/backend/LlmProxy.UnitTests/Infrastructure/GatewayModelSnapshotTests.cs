using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.UnitTests.Infrastructure;

public sealed class GatewayModelSnapshotTests
{
    [Fact]
    public void Hand_maintained_migrations_and_snapshot_match_the_current_model()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql("Host=localhost;Database=probe;Username=x;Password=x").Options;
        // Migrations and the snapshot are hand-written; this fails when a model change lacks its migration.
        using var db = new GatewayDbContext(options);
#pragma warning disable EF1001, EF9001
        Assert.False(db.Database.HasPendingModelChanges(), "The EF model differs from GatewayDbContextModelSnapshot.");
#pragma warning restore EF1001, EF9001
    }
}
