using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261113120000_AddCorsOrigins")]
public partial class AddCorsOrigins : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("AllowedOrigins", "api_credentials", type: "text", nullable: true);
        migrationBuilder.CreateTable(
            name: "cors_settings",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                AllowedOrigins = table.Column<string>(type: "text", nullable: true),
                MaxAgeSeconds = table.Column<int>(type: "integer", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_cors_settings", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("cors_settings");
        migrationBuilder.DropColumn("AllowedOrigins", "api_credentials");
    }
}
