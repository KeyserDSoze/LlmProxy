using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260914150500_AddUsageGovernance")]
public partial class AddUsageGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "usage_groups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_usage_groups", x => x.Id));

        migrationBuilder.AddColumn<Guid>(
            name: "UsageGroupId",
            table: "api_credentials",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "UsageGroupId",
            table: "request_metrics",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "rate_limit_policies",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ApiCredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                LogicalModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                RequestsPerWindow = table.Column<int>(type: "integer", nullable: false),
                WindowSeconds = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_rate_limit_policies", x => x.Id);
                table.ForeignKey(
                    name: "FK_rate_limit_policies_api_credentials_ApiCredentialId",
                    column: x => x.ApiCredentialId,
                    principalTable: "api_credentials",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_usage_groups_Name",
            table: "usage_groups",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_credentials_UsageGroupId",
            table: "api_credentials",
            column: "UsageGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_request_metrics_UsageGroupId_StartedAtUtc",
            table: "request_metrics",
            columns: new[] { "UsageGroupId", "StartedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_rate_limit_policies_ApiCredentialId",
            table: "rate_limit_policies",
            column: "ApiCredentialId");

        migrationBuilder.CreateIndex(
            name: "IX_rate_limit_policies_ApiCredentialId_LogicalModel",
            table: "rate_limit_policies",
            columns: new[] { "ApiCredentialId", "LogicalModel" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_api_credentials_usage_groups_UsageGroupId",
            table: "api_credentials",
            column: "UsageGroupId",
            principalTable: "usage_groups",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_api_credentials_usage_groups_UsageGroupId",
            table: "api_credentials");

        migrationBuilder.DropTable(name: "rate_limit_policies");
        migrationBuilder.DropIndex(name: "IX_api_credentials_UsageGroupId", table: "api_credentials");
        migrationBuilder.DropIndex(name: "IX_request_metrics_UsageGroupId_StartedAtUtc", table: "request_metrics");
        migrationBuilder.DropColumn(name: "UsageGroupId", table: "api_credentials");
        migrationBuilder.DropColumn(name: "UsageGroupId", table: "request_metrics");
        migrationBuilder.DropTable(name: "usage_groups");
    }
}
