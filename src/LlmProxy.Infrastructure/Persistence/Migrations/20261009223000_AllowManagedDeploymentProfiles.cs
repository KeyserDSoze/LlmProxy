using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261009223000_AllowManagedDeploymentProfiles")]
public partial class AllowManagedDeploymentProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_deployments_NodeId_ModelId", table: "deployments");
        migrationBuilder.CreateIndex(name: "IX_deployments_NodeId_ModelId", table: "deployments",
            columns: new[] { "NodeId", "ModelId" }, unique: true,
            filter: "\"ManagedInstallationId\" IS NULL");
        migrationBuilder.CreateIndex(name: "IX_deployments_NodeId_ManagedInstallationId", table: "deployments",
            columns: new[] { "NodeId", "ManagedInstallationId" }, unique: true,
            filter: "\"ManagedInstallationId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM deployments
        GROUP BY "NodeId", "ModelId"
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Cannot downgrade: multiple managed deployments share one model/node.';
    END IF;
END $$;
""");
        migrationBuilder.DropIndex(name: "IX_deployments_NodeId_ManagedInstallationId", table: "deployments");
        migrationBuilder.DropIndex(name: "IX_deployments_NodeId_ModelId", table: "deployments");
        migrationBuilder.CreateIndex(name: "IX_deployments_NodeId_ModelId", table: "deployments",
            columns: new[] { "NodeId", "ModelId" }, unique: true);
    }
}
