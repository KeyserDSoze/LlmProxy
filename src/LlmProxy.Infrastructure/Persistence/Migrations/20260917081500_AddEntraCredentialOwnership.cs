using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260917081500_AddEntraCredentialOwnership")]
public partial class AddEntraCredentialOwnership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OwnerObjectId",
            table: "api_credentials",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OwnerPrincipalName",
            table: "api_credentials",
            type: "character varying(320)",
            maxLength: 320,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OwnerTenantId",
            table: "api_credentials",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_credentials_OwnerTenantId_OwnerObjectId",
            table: "api_credentials",
            columns: new[] { "OwnerTenantId", "OwnerObjectId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_api_credentials_OwnerTenantId_OwnerObjectId",
            table: "api_credentials");

        migrationBuilder.DropColumn(name: "OwnerObjectId", table: "api_credentials");
        migrationBuilder.DropColumn(name: "OwnerPrincipalName", table: "api_credentials");
        migrationBuilder.DropColumn(name: "OwnerTenantId", table: "api_credentials");
    }
}
