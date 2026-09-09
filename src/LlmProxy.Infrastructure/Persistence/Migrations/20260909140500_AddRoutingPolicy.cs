using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260909140500_AddRoutingPolicy")]
public partial class AddRoutingPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "routing_policy",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                Strategy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_routing_policy", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "routing_policy");
    }
}
