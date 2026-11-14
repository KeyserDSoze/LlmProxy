using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261114120000_AddRuntimeEnginePolicies")]
public partial class AddRuntimeEnginePolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "runtime_engine_policies",
            columns: table => new
            {
                NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                EngineId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                AutoUpdate = table.Column<bool>(type: "boolean", nullable: false),
                UpdateAtLocal = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastRunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastResult = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                LastMessage = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_runtime_engine_policies", x => new { x.NodeId, x.EngineId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("runtime_engine_policies");
}
