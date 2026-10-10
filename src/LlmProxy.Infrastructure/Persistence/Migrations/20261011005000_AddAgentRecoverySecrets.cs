using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261011005000_AddAgentRecoverySecrets")]
public partial class AddAgentRecoverySecrets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("RecoverySecretHash", "node_enrollments",
            type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("RecoverySecretCiphertext", "node_enrollments",
            type: "character varying(4096)", maxLength: 4096, nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("RecoverySecretCreatedAtUtc", "node_enrollments",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.CreateIndex("IX_node_enrollments_RecoverySecretHash",
            "node_enrollments", "RecoverySecretHash", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_node_enrollments_RecoverySecretHash", "node_enrollments");
        migrationBuilder.DropColumn("RecoverySecretHash", "node_enrollments");
        migrationBuilder.DropColumn("RecoverySecretCiphertext", "node_enrollments");
        migrationBuilder.DropColumn("RecoverySecretCreatedAtUtc", "node_enrollments");
    }
}
