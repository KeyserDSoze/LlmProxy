using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261009233000_AddNodeEnrollment")]
public partial class AddNodeEnrollment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "node_enrollments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                InvitationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                AgentSecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                HardwareInventoryJson = table.Column<string>(type: "text", nullable: true),
                LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AgentVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_node_enrollments", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_node_enrollments_InvitationHash", table: "node_enrollments", column: "InvitationHash", unique: true);
        migrationBuilder.CreateIndex(name: "IX_node_enrollments_AgentSecretHash", table: "node_enrollments", column: "AgentSecretHash", unique: true);
        migrationBuilder.CreateIndex(name: "IX_node_enrollments_NodeId", table: "node_enrollments", column: "NodeId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("node_enrollments");
}
