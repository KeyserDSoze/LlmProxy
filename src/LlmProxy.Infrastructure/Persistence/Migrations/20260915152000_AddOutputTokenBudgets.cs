using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260915152000_AddOutputTokenBudgets")]
public partial class AddOutputTokenBudgets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "OutputTokensPerWindow",
            table: "rate_limit_policies",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxOutputTokensPerRequest",
            table: "rate_limit_policies",
            type: "integer",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "OutputTokensPerWindow", table: "rate_limit_policies");
        migrationBuilder.DropColumn(name: "MaxOutputTokensPerRequest", table: "rate_limit_policies");
    }
}
