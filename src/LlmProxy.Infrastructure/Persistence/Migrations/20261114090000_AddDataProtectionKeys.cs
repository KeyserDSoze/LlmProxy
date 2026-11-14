using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261114090000_AddDataProtectionKeys")]
public partial class AddDataProtectionKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "data_protection_keys",
            columns: table => new
            {
                FriendlyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                XmlCiphertext = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_data_protection_keys", x => x.FriendlyName));
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("data_protection_keys");
}
