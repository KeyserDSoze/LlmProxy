using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260913074500_AddDeploymentCapacityProfile")]
public partial class AddDeploymentCapacityProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "RecommendedMaxConcurrency",
            table: "deployments",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "BenchmarkP95TtftMilliseconds",
            table: "deployments",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "BenchmarkP95DurationMilliseconds",
            table: "deployments",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "SustainableOutputTokensPerSecond",
            table: "deployments",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BenchmarkSource",
            table: "deployments",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "BenchmarkMeasuredAtUtc",
            table: "deployments",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RecommendedMaxConcurrency", table: "deployments");
        migrationBuilder.DropColumn(name: "BenchmarkP95TtftMilliseconds", table: "deployments");
        migrationBuilder.DropColumn(name: "BenchmarkP95DurationMilliseconds", table: "deployments");
        migrationBuilder.DropColumn(name: "SustainableOutputTokensPerSecond", table: "deployments");
        migrationBuilder.DropColumn(name: "BenchmarkSource", table: "deployments");
        migrationBuilder.DropColumn(name: "BenchmarkMeasuredAtUtc", table: "deployments");
    }
}
