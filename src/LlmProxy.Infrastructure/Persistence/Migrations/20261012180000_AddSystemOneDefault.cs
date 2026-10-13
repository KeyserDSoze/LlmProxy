using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261012180000_AddSystemOneDefault")]
public partial class AddSystemOneDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "systemone_default",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                PublicModelName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_systemone_default", x => x.Id));
        migrationBuilder.AddCheckConstraint("CK_systemone_default_singleton",
            "systemone_default", "\"Id\" = 1");
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("systemone_default");
}
