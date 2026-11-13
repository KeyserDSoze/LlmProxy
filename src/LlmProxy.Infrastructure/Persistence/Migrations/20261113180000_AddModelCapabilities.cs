using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261113180000_AddModelCapabilities")]
public partial class AddModelCapabilities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("SupportsVision", "models", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>("SupportsReasoning", "models", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<int>("ContextWindowTokens", "models", type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>("MaxOutputTokens", "models", type: "integer", nullable: true);
        migrationBuilder.CreateTable(
            name: "managed_install_declarations",
            columns: table => new
            {
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                DeclaredJson = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_managed_install_declarations", x => x.JobId));
        migrationBuilder.CreateIndex("IX_managed_install_declarations_CreatedAtUtc", "managed_install_declarations", "CreatedAtUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("managed_install_declarations");
        migrationBuilder.DropColumn("MaxOutputTokens", "models");
        migrationBuilder.DropColumn("ContextWindowTokens", "models");
        migrationBuilder.DropColumn("SupportsReasoning", "models");
        migrationBuilder.DropColumn("SupportsVision", "models");
    }
}
