using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260909143000_AddHealthDiagnosticsAndAudit")]
public partial class AddHealthDiagnosticsAndAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ConsecutiveHealthFailures",
            table: "nodes",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ConsecutiveHealthSuccesses",
            table: "nodes",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "LastHealthError",
            table: "nodes",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "LastHealthLatencyMilliseconds",
            table: "nodes",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastHealthyAtUtc",
            table: "nodes",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "audit_events",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                EntityId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                SourceIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                DetailsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_events", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_audit_events_EntityType_EntityId",
            table: "audit_events",
            columns: new[] { "EntityType", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_events_OccurredAtUtc",
            table: "audit_events",
            column: "OccurredAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "audit_events");

        migrationBuilder.DropColumn(name: "ConsecutiveHealthFailures", table: "nodes");
        migrationBuilder.DropColumn(name: "ConsecutiveHealthSuccesses", table: "nodes");
        migrationBuilder.DropColumn(name: "LastHealthError", table: "nodes");
        migrationBuilder.DropColumn(name: "LastHealthLatencyMilliseconds", table: "nodes");
        migrationBuilder.DropColumn(name: "LastHealthyAtUtc", table: "nodes");
    }
}
