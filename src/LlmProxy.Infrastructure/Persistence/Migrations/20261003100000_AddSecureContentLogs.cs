using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261003100000_AddSecureContentLogs")]
public partial class AddSecureContentLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SecretCiphertext",
            table: "api_credentials",
            type: "character varying(4096)",
            maxLength: 4096,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "content_log_settings",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                RetentionDays = table.Column<int>(type: "integer", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_content_log_settings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "inference_content_logs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Surface = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                LogicalModel = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                ApiCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                StatusCode = table.Column<int>(type: "integer", nullable: false),
                RequestContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ResponseContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                RequestBodyCiphertext = table.Column<string>(type: "text", nullable: false),
                ResponseBodyCiphertext = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inference_content_logs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_inference_content_logs_RequestId",
            table: "inference_content_logs",
            column: "RequestId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_inference_content_logs_StartedAtUtc",
            table: "inference_content_logs",
            column: "StartedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_inference_content_logs_Surface_StartedAtUtc",
            table: "inference_content_logs",
            columns: new[] { "Surface", "StartedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "inference_content_logs");
        migrationBuilder.DropTable(name: "content_log_settings");
        migrationBuilder.DropColumn(name: "SecretCiphertext", table: "api_credentials");
    }
}
