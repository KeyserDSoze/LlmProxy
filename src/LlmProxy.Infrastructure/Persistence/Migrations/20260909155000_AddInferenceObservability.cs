using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260909155000_AddInferenceObservability")]
public partial class AddInferenceObservability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            table: "request_metrics",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "InputTokens",
            table: "request_metrics",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsStreaming",
            table: "request_metrics",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "OutputTokens",
            table: "request_metrics",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Surface",
            table: "request_metrics",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "unknown");

        migrationBuilder.AddColumn<long>(
            name: "TimeToFirstByteMilliseconds",
            table: "request_metrics",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "TotalTokens",
            table: "request_metrics",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "UpstreamHeaderMilliseconds",
            table: "request_metrics",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_request_metrics_LogicalModel_StartedAtUtc",
            table: "request_metrics",
            columns: new[] { "LogicalModel", "StartedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_request_metrics_NodeId_StartedAtUtc",
            table: "request_metrics",
            columns: new[] { "NodeId", "StartedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_request_metrics_LogicalModel_StartedAtUtc",
            table: "request_metrics");

        migrationBuilder.DropIndex(
            name: "IX_request_metrics_NodeId_StartedAtUtc",
            table: "request_metrics");

        migrationBuilder.DropColumn(name: "AttemptCount", table: "request_metrics");
        migrationBuilder.DropColumn(name: "InputTokens", table: "request_metrics");
        migrationBuilder.DropColumn(name: "IsStreaming", table: "request_metrics");
        migrationBuilder.DropColumn(name: "OutputTokens", table: "request_metrics");
        migrationBuilder.DropColumn(name: "Surface", table: "request_metrics");
        migrationBuilder.DropColumn(name: "TimeToFirstByteMilliseconds", table: "request_metrics");
        migrationBuilder.DropColumn(name: "TotalTokens", table: "request_metrics");
        migrationBuilder.DropColumn(name: "UpstreamHeaderMilliseconds", table: "request_metrics");
    }
}
