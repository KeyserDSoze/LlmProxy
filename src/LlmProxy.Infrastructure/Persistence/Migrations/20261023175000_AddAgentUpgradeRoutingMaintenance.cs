using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261023175000_AddAgentUpgradeRoutingMaintenance")]
public partial class AddAgentUpgradeRoutingMaintenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AgentUpgradeTargetVersion", "node_enrollments",
            type: "character varying(40)", maxLength: 40, nullable: true);
        migrationBuilder.AddColumn<string>("AgentUpgradeMaintenanceStage", "node_enrollments",
            type: "character varying(48)", maxLength: 48, nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("AgentUpgradeStartedAtUtc", "node_enrollments",
            type: "timestamp with time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AgentUpgradeTargetVersion", "node_enrollments");
        migrationBuilder.DropColumn("AgentUpgradeMaintenanceStage", "node_enrollments");
        migrationBuilder.DropColumn("AgentUpgradeStartedAtUtc", "node_enrollments");
    }
}
