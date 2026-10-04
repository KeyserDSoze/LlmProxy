using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261004081500_AddRequestAuditSummaries")]
public partial class AddRequestAuditSummaries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SummaryDefaultLogicalModel",
            table: "content_log_settings",
            type: "character varying(160)",
            maxLength: 160,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SummaryDefaultNodeId",
            table: "content_log_settings",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SummarySystemPrompt",
            table: "content_log_settings",
            type: "text",
            nullable: false,
            defaultValue: ContentLogSettingsRecord.DefaultSummarySystemPrompt);

        migrationBuilder.CreateTable(
            name: "request_audit_summaries",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ContentLogId = table.Column<long>(type: "bigint", nullable: false),
                SummaryCiphertext = table.Column<string>(type: "text", nullable: false),
                LogicalModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                GeneratedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                GeneratedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_request_audit_summaries", x => x.Id);
                table.ForeignKey(
                    name: "FK_request_audit_summaries_inference_content_logs_ContentLogId",
                    column: x => x.ContentLogId,
                    principalTable: "inference_content_logs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_request_audit_summaries_ContentLogId",
            table: "request_audit_summaries",
            column: "ContentLogId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_request_audit_summaries_UpdatedAtUtc",
            table: "request_audit_summaries",
            column: "UpdatedAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "request_audit_summaries");

        migrationBuilder.DropColumn(name: "SummaryDefaultLogicalModel", table: "content_log_settings");
        migrationBuilder.DropColumn(name: "SummaryDefaultNodeId", table: "content_log_settings");
        migrationBuilder.DropColumn(name: "SummarySystemPrompt", table: "content_log_settings");
    }
}
