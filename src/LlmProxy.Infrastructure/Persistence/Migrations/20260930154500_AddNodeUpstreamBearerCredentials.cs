using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260930154500_AddNodeUpstreamBearerCredentials")]
public partial class AddNodeUpstreamBearerCredentials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "UpstreamBearerTokenCiphertext",
            table: "nodes",
            type: "character varying(4096)",
            maxLength: 4096,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "UpstreamBearerTokenCiphertext", table: "nodes");
    }
}
