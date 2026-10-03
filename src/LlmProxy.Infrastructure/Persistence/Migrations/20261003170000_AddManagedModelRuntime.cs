using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261003170000_AddManagedModelRuntime")]
public partial class AddManagedModelRuntime : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ManagementBaseAddress",
            table: "nodes",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ManagementBearerTokenCiphertext",
            table: "nodes",
            type: "character varying(4096)",
            maxLength: 4096,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RuntimeBaseAddress",
            table: "deployments",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CatalogModelId",
            table: "deployments",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ManagedInstallationId",
            table: "deployments",
            type: "character varying(300)",
            maxLength: 300,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ManagementBaseAddress", table: "nodes");
        migrationBuilder.DropColumn(name: "ManagementBearerTokenCiphertext", table: "nodes");
        migrationBuilder.DropColumn(name: "RuntimeBaseAddress", table: "deployments");
        migrationBuilder.DropColumn(name: "CatalogModelId", table: "deployments");
        migrationBuilder.DropColumn(name: "ManagedInstallationId", table: "deployments");
    }
}
