using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261114150000_AddRuntimeEnginePolicyRestartModels")]
public partial class AddRuntimeEnginePolicyRestartModels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(name: "RestartModels", table: "runtime_engine_policies", type: "boolean", nullable: false, defaultValue: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "RestartModels", table: "runtime_engine_policies");
}
