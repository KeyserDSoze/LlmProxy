using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261003150000_AddUserGroupGovernance")]
public partial class AddUserGroupGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "EnforceCallerGovernance",
            table: "api_credentials",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE api_credentials
            SET "EnforceCallerGovernance" = TRUE
            WHERE "OwnerTenantId" IS NOT NULL
              AND "OwnerObjectId" IS NOT NULL;
            """);

        migrationBuilder.AddColumn<int>(
            name: "OutputTokensPerWindow",
            table: "user_rate_limit_policies",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxOutputTokensPerRequest",
            table: "user_rate_limit_policies",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "UsageGroupId",
            table: "platform_users",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "usage_group_rate_limit_policies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UsageGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                LogicalModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                RequestsPerWindow = table.Column<int>(type: "integer", nullable: false),
                WindowSeconds = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                OutputTokensPerWindow = table.Column<int>(type: "integer", nullable: true),
                MaxOutputTokensPerRequest = table.Column<int>(type: "integer", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_usage_group_rate_limit_policies", x => x.Id);
                table.ForeignKey(
                    name: "FK_usage_group_rate_limit_policies_usage_groups_UsageGroupId",
                    column: x => x.UsageGroupId,
                    principalTable: "usage_groups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_platform_users_UsageGroupId",
            table: "platform_users",
            column: "UsageGroupId");

        migrationBuilder.AddForeignKey(
            name: "FK_platform_users_usage_groups_UsageGroupId",
            table: "platform_users",
            column: "UsageGroupId",
            principalTable: "usage_groups",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.CreateIndex(
            name: "IX_usage_group_rate_limit_policies_UsageGroupId",
            table: "usage_group_rate_limit_policies",
            column: "UsageGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_usage_group_rate_limit_policies_UsageGroupId_LogicalModel",
            table: "usage_group_rate_limit_policies",
            columns: new[] { "UsageGroupId", "LogicalModel" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "usage_group_rate_limit_policies");

        migrationBuilder.DropForeignKey(
            name: "FK_platform_users_usage_groups_UsageGroupId",
            table: "platform_users");

        migrationBuilder.DropIndex(
            name: "IX_platform_users_UsageGroupId",
            table: "platform_users");

        migrationBuilder.DropColumn(
            name: "UsageGroupId",
            table: "platform_users");

        migrationBuilder.DropColumn(
            name: "OutputTokensPerWindow",
            table: "user_rate_limit_policies");

        migrationBuilder.DropColumn(
            name: "MaxOutputTokensPerRequest",
            table: "user_rate_limit_policies");

        migrationBuilder.DropColumn(
            name: "EnforceCallerGovernance",
            table: "api_credentials");
    }
}
