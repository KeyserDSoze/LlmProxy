using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20261009234500_AddInferenceBenchmarkJobs")]
public partial class AddInferenceBenchmarkJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("inference_benchmark_jobs", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
            Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            MaxP95TtftMilliseconds = table.Column<double>(type: "double precision", nullable: false),
            MinSuccessRatePercent = table.Column<double>(type: "double precision", nullable: false),
            ReportJson = table.Column<string>(type: "text", nullable: true),
            Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_inference_benchmark_jobs", x => x.Id);
            table.ForeignKey("FK_inference_benchmark_jobs_deployments_DeploymentId",
                x => x.DeploymentId, "deployments", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_inference_benchmark_jobs_Status", "inference_benchmark_jobs", "Status");
        migrationBuilder.CreateIndex("IX_inference_benchmark_jobs_DeploymentId_RequestedAtUtc",
            "inference_benchmark_jobs", new[] { "DeploymentId", "RequestedAtUtc" });
        migrationBuilder.CreateIndex("IX_inference_benchmark_jobs_ActiveDeployment",
            "inference_benchmark_jobs", "DeploymentId", unique: true,
            filter: "\"Status\" IN ('pending', 'running')");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("inference_benchmark_jobs");
}
