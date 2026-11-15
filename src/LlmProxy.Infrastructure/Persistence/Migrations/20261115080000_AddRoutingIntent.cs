using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261115080000_AddRoutingIntent")]
public sealed class AddRoutingIntent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "RoutingDesired", table: "deployments", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<Guid>(name: "ActivationStartJobId", table: "deployments", type: "uuid", nullable: true);
        // Do not reactivate installations deliberately stopped by an administrator.
        migrationBuilder.Sql("UPDATE deployments SET \"RoutingDesired\" = \"Enabled\"");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RoutingDesired", table: "deployments");
        migrationBuilder.DropColumn(name: "ActivationStartJobId", table: "deployments");
    }
}
