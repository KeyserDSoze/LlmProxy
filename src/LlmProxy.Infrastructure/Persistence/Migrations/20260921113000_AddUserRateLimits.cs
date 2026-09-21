using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260921113000_AddUserRateLimits")]
public partial class AddUserRateLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "user_rate_limit_policies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerTenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                OwnerObjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                LogicalModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                RequestsPerWindow = table.Column<int>(type: "integer", nullable: false),
                WindowSeconds = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_rate_limit_policies", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_user_rate_limit_policies_OwnerTenantId_OwnerObjectId",
            table: "user_rate_limit_policies",
            columns: new[] { "OwnerTenantId", "OwnerObjectId" });

        migrationBuilder.CreateIndex(
            name: "IX_user_rate_limit_policies_OwnerTenantId_OwnerObjectId_LogicalModel",
            table: "user_rate_limit_policies",
            columns: new[] { "OwnerTenantId", "OwnerObjectId", "LogicalModel" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "user_rate_limit_policies");
    }
}
