using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260916103000_AddDailyUsageRollups")]
public partial class AddDailyUsageRollups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "daily_usage_rollups",
            columns: table => new
            {
                DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                ApiCredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                UsageGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                LogicalModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                RequestCount = table.Column<long>(type: "bigint", nullable: false),
                ErrorCount = table.Column<long>(type: "bigint", nullable: false),
                InputTokens = table.Column<long>(type: "bigint", nullable: false),
                OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                TotalTokens = table.Column<long>(type: "bigint", nullable: false),
                RateLimitedRequests = table.Column<long>(type: "bigint", nullable: false),
                CapacityExhaustedRequests = table.Column<long>(type: "bigint", nullable: false),
                DurationMillisecondsTotal = table.Column<long>(type: "bigint", nullable: false),
                TtftMillisecondsTotal = table.Column<long>(type: "bigint", nullable: false),
                TtftSampleCount = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_daily_usage_rollups",
                    x => new { x.DayUtc, x.ApiCredentialId, x.UsageGroupId, x.LogicalModel });
            });

        migrationBuilder.CreateIndex(
            name: "IX_daily_usage_rollups_ApiCredentialId_DayUtc",
            table: "daily_usage_rollups",
            columns: new[] { "ApiCredentialId", "DayUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_daily_usage_rollups_LogicalModel_DayUtc",
            table: "daily_usage_rollups",
            columns: new[] { "LogicalModel", "DayUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_daily_usage_rollups_UsageGroupId_DayUtc",
            table: "daily_usage_rollups",
            columns: new[] { "UsageGroupId", "DayUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "daily_usage_rollups");
    }
}
