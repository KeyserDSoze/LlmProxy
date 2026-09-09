using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260909104500_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "api_credentials",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                KeyPrefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                KeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastUsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_api_credentials", x => x.Id));

        migrationBuilder.CreateTable(
            name: "models",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PublicName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                ProviderModelName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                SupportsStreaming = table.Column<bool>(type: "boolean", nullable: false),
                SupportsTools = table.Column<bool>(type: "boolean", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_models", x => x.Id));

        migrationBuilder.CreateTable(
            name: "nodes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                BaseAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                Weight = table.Column<int>(type: "integer", nullable: false),
                MaxConcurrency = table.Column<int>(type: "integer", nullable: false),
                LastHealthCheckUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_nodes", x => x.Id));

        migrationBuilder.CreateTable(
            name: "request_metrics",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LogicalModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                ApiCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                StatusCode = table.Column<int>(type: "integer", nullable: false),
                DurationMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_request_metrics", x => x.Id));

        migrationBuilder.CreateTable(
            name: "deployments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                Weight = table.Column<int>(type: "integer", nullable: false),
                MaxConcurrency = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_deployments", x => x.Id);
                table.ForeignKey("FK_deployments_models_ModelId", x => x.ModelId, "models", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_deployments_nodes_NodeId", x => x.NodeId, "nodes", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_api_credentials_KeyHash", table: "api_credentials", column: "KeyHash", unique: true);
        migrationBuilder.CreateIndex(name: "IX_api_credentials_KeyPrefix", table: "api_credentials", column: "KeyPrefix");
        migrationBuilder.CreateIndex(name: "IX_deployments_ModelId", table: "deployments", column: "ModelId");
        migrationBuilder.CreateIndex(name: "IX_deployments_NodeId_ModelId", table: "deployments", columns: new[] { "NodeId", "ModelId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_models_PublicName", table: "models", column: "PublicName", unique: true);
        migrationBuilder.CreateIndex(name: "IX_nodes_Name", table: "nodes", column: "Name", unique: true);
        migrationBuilder.CreateIndex(name: "IX_request_metrics_ApiCredentialId", table: "request_metrics", column: "ApiCredentialId");
        migrationBuilder.CreateIndex(name: "IX_request_metrics_RequestId", table: "request_metrics", column: "RequestId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_request_metrics_StartedAtUtc", table: "request_metrics", column: "StartedAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "api_credentials");
        migrationBuilder.DropTable(name: "deployments");
        migrationBuilder.DropTable(name: "request_metrics");
        migrationBuilder.DropTable(name: "models");
        migrationBuilder.DropTable(name: "nodes");
    }
}
