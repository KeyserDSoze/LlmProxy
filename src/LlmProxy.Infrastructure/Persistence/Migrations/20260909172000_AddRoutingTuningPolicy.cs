using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
[Migration("20260909172000_AddRoutingTuningPolicy")]
public partial class AddRoutingTuningPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "routing_tuning_policy",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                WarmupSamples = table.Column<int>(type: "integer", nullable: false),
                TtftTargetMilliseconds = table.Column<double>(type: "double precision", nullable: false),
                TtftPenaltyWeight = table.Column<double>(type: "double precision", nullable: false),
                FailurePenaltyWeight = table.Column<double>(type: "double precision", nullable: false),
                ExternalLoadPenaltyWeight = table.Column<double>(type: "double precision", nullable: false),
                QueuePenaltyWeight = table.Column<double>(type: "double precision", nullable: false),
                KvCacheThreshold = table.Column<double>(type: "double precision", nullable: false),
                KvCachePenaltyWeight = table.Column<double>(type: "double precision", nullable: false),
                DegradedNodePenalty = table.Column<double>(type: "double precision", nullable: false),
                UnknownNodePenalty = table.Column<double>(type: "double precision", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_routing_tuning_policy", x => x.Id);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "routing_tuning_policy");
    }
}
