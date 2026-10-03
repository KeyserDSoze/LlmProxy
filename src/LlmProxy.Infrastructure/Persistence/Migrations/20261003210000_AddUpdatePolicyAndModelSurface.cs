using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261003210000_AddUpdatePolicyAndModelSurface")]
public partial class AddUpdatePolicyAndModelSurface : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "Surface", table: "models", type: "integer", nullable: false, defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "product_update_policy",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                LocalHour = table.Column<int>(type: "integer", nullable: false),
                LocalMinute = table.Column<int>(type: "integer", nullable: false),
                DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                DayOfMonth = table.Column<int>(type: "integer", nullable: false),
                LastCheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastScheduledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastScheduledVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                LastError = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_product_update_policy", x => x.Id));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "product_update_policy");
        migrationBuilder.DropColumn(name: "Surface", table: "models");
    }
}
