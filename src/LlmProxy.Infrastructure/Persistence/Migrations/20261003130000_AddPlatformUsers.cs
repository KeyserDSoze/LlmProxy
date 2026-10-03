using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261003130000_AddPlatformUsers")]
public partial class AddPlatformUsers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "platform_users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ObjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PrincipalName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                DisplayName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                ProvisioningSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DisabledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_platform_users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "user_access_settings",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                ProvisioningMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_access_settings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_platform_users_PrincipalName",
            table: "platform_users",
            column: "PrincipalName");

        migrationBuilder.CreateIndex(
            name: "IX_platform_users_TenantId_ObjectId",
            table: "platform_users",
            columns: new[] { "TenantId", "ObjectId" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "platform_users");
        migrationBuilder.DropTable(name: "user_access_settings");
    }
}
