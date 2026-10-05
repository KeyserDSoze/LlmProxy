using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace LlmProxy.Infrastructure.Persistence.Migrations;

[DbContext(typeof(GatewayDbContext))]
partial class GatewayDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.12")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("LlmProxy.Domain.Audit.AuditEvent", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            b.Property<string>("Action").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("Actor").IsRequired().HasMaxLength(320).HasColumnType("character varying(320)");
            b.Property<string>("DetailsJson").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("EntityId").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("EntityType").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset>("OccurredAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("SourceIp").HasMaxLength(64).HasColumnType("character varying(64)");
            b.HasKey("Id");
            b.HasIndex("OccurredAtUtc");
            b.HasIndex("EntityType", "EntityId");
            b.ToTable("audit_events");
        });

        modelBuilder.Entity("LlmProxy.Domain.Deployments.ModelDeployment", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset?>("BenchmarkMeasuredAtUtc").HasColumnType("timestamp with time zone");
            b.Property<double?>("BenchmarkP95DurationMilliseconds").HasColumnType("double precision");
            b.Property<double?>("BenchmarkP95TtftMilliseconds").HasColumnType("double precision");
            b.Property<string>("BenchmarkSource").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<int?>("MaxConcurrency").HasColumnType("integer");
            b.Property<string>("RuntimeBaseAddress").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("UpstreamBearerTokenCiphertext").HasMaxLength(4096).HasColumnType("character varying(4096)");
            b.Property<string>("CatalogModelId").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("ManagedInstallationId").HasMaxLength(300).HasColumnType("character varying(300)");
            b.Property<Guid>("ModelId").HasColumnType("uuid");
            b.Property<Guid>("NodeId").HasColumnType("uuid");
            b.Property<int?>("RecommendedMaxConcurrency").HasColumnType("integer");
            b.Property<double?>("SustainableOutputTokensPerSecond").HasColumnType("double precision");
            b.Property<int>("Weight").HasColumnType("integer");
            b.HasKey("Id");
            b.HasIndex("ModelId");
            b.HasIndex("NodeId", "ModelId").IsUnique();
            b.ToTable("deployments");
        });

        modelBuilder.Entity("LlmProxy.Domain.Governance.RateLimitPolicy", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("ApiCredentialId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("LogicalModel").HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<int?>("MaxOutputTokensPerRequest").HasColumnType("integer");
            b.Property<int?>("OutputTokensPerWindow").HasColumnType("integer");
            b.Property<int>("RequestsPerWindow").HasColumnType("integer");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("WindowSeconds").HasColumnType("integer");
            b.HasKey("Id");
            b.HasIndex("ApiCredentialId");
            b.HasIndex("ApiCredentialId", "LogicalModel").IsUnique();
            b.ToTable("rate_limit_policies");
        });

        modelBuilder.Entity("LlmProxy.Domain.Governance.UserRateLimitPolicy", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("LogicalModel").HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<string>("OwnerObjectId").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("OwnerTenantId").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<int?>("MaxOutputTokensPerRequest").HasColumnType("integer");
            b.Property<int?>("OutputTokensPerWindow").HasColumnType("integer");
            b.Property<int>("RequestsPerWindow").HasColumnType("integer");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("WindowSeconds").HasColumnType("integer");
            b.HasKey("Id");
            b.HasIndex("OwnerTenantId", "OwnerObjectId");
            b.HasIndex("OwnerTenantId", "OwnerObjectId", "LogicalModel");
            b.ToTable("user_rate_limit_policies");
        });

        modelBuilder.Entity("LlmProxy.Domain.Governance.UsageGroupRateLimitPolicy", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("LogicalModel").HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<int?>("MaxOutputTokensPerRequest").HasColumnType("integer");
            b.Property<int?>("OutputTokensPerWindow").HasColumnType("integer");
            b.Property<int>("RequestsPerWindow").HasColumnType("integer");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("UsageGroupId").HasColumnType("uuid");
            b.Property<int>("WindowSeconds").HasColumnType("integer");
            b.HasKey("Id");
            b.HasIndex("UsageGroupId");
            b.HasIndex("UsageGroupId", "LogicalModel").IsUnique();
            b.ToTable("usage_group_rate_limit_policies");
        });

        modelBuilder.Entity("LlmProxy.Domain.Governance.UsageGroup", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("Description").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("Name").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("Name").IsUnique();
            b.ToTable("usage_groups");
        });

        modelBuilder.Entity("LlmProxy.Domain.Models.ModelDefinition", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("ProviderModelName").IsRequired().HasMaxLength(300).HasColumnType("character varying(300)");
            b.Property<string>("PublicName").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<int>("Surface").HasColumnType("integer");
            b.Property<bool>("SupportsStreaming").HasColumnType("boolean");
            b.Property<bool>("SupportsTools").HasColumnType("boolean");
            b.HasKey("Id");
            b.HasIndex("PublicName").IsUnique();
            b.ToTable("models");
        });

        modelBuilder.Entity("LlmProxy.Domain.Nodes.InferenceNode", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("BaseAddress").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<int>("ConsecutiveHealthFailures").HasColumnType("integer");
            b.Property<int>("ConsecutiveHealthSuccesses").HasColumnType("integer");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("HardwareMetricsBaseAddress").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("ManagementBaseAddress").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("ManagementBearerTokenCiphertext").HasMaxLength(4096).HasColumnType("character varying(4096)");
            b.Property<DateTimeOffset?>("LastHealthCheckUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("LastHealthError").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<long?>("LastHealthLatencyMilliseconds").HasColumnType("bigint");
            b.Property<DateTimeOffset?>("LastHealthyAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("MaxConcurrency").HasColumnType("integer");
            b.Property<string>("Name").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<int>("Status").HasColumnType("integer");
            b.Property<string>("UpstreamBearerTokenCiphertext").HasMaxLength(4096).HasColumnType("character varying(4096)");
            b.Property<int>("Weight").HasColumnType("integer");
            b.HasKey("Id");
            b.HasIndex("Name").IsUnique();
            b.ToTable("nodes");
        });

        modelBuilder.Entity("LlmProxy.Domain.Routing.RoutingPolicy", b =>
        {
            b.Property<int>("Id").HasColumnType("integer");
            b.Property<string>("Strategy").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.ToTable("routing_policy");
        });

        modelBuilder.Entity("LlmProxy.Domain.Routing.RoutingTuningPolicy", b =>
        {
            b.Property<int>("Id").HasColumnType("integer");
            b.Property<double>("DegradedNodePenalty").HasColumnType("double precision");
            b.Property<double>("ExternalLoadPenaltyWeight").HasColumnType("double precision");
            b.Property<double>("FailurePenaltyWeight").HasColumnType("double precision");
            b.Property<double>("KvCachePenaltyWeight").HasColumnType("double precision");
            b.Property<double>("KvCacheThreshold").HasColumnType("double precision");
            b.Property<double>("QueuePenaltyWeight").HasColumnType("double precision");
            b.Property<double>("TtftPenaltyWeight").HasColumnType("double precision");
            b.Property<double>("TtftTargetMilliseconds").HasColumnType("double precision");
            b.Property<double>("UnknownNodePenalty").HasColumnType("double precision");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("WarmupSamples").HasColumnType("integer");
            b.HasKey("Id");
            b.ToTable("routing_tuning_policy");
        });

        modelBuilder.Entity("LlmProxy.Domain.Governance.UsageGroupRateLimitPolicy", b =>
        {
            b.HasOne("LlmProxy.Domain.Governance.UsageGroup", null)
                .WithMany()
                .HasForeignKey("UsageGroupId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("LlmProxy.Domain.Security.ApiCredential", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<bool>("EnforceCallerGovernance").HasColumnType("boolean");
            b.Property<DateTimeOffset?>("ExpiresAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("KeyHash").IsRequired().HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("KeyPrefix").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("SecretCiphertext").HasMaxLength(4096).HasColumnType("character varying(4096)");
            b.Property<DateTimeOffset?>("LastUsedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("Name").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<string>("OwnerObjectId").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("OwnerPrincipalName").HasMaxLength(320).HasColumnType("character varying(320)");
            b.Property<string>("OwnerTenantId").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<Guid?>("UsageGroupId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("KeyHash").IsUnique();
            b.HasIndex("KeyPrefix");
            b.HasIndex("UsageGroupId");
            b.HasIndex("OwnerTenantId", "OwnerObjectId");
            b.ToTable("api_credentials");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.ContentLogSettingsRecord", b =>
        {
            b.Property<int>("Id").HasColumnType("integer");
            b.Property<int>("RetentionDays").HasColumnType("integer");
            b.Property<string>("SummaryDefaultLogicalModel").HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<Guid?>("SummaryDefaultNodeId").HasColumnType("uuid");
            b.Property<string>("SummarySystemPrompt").IsRequired().HasColumnType("text");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.ToTable("content_log_settings");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.InferenceContentLogRecord", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            b.Property<Guid?>("ApiCredentialId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CompletedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("LogicalModel").HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<string>("Method").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<string>("Path").IsRequired().HasMaxLength(300).HasColumnType("character varying(300)");
            b.Property<string>("RequestBodyCiphertext").IsRequired().HasColumnType("text");
            b.Property<string>("RequestContentType").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<Guid>("RequestId").HasColumnType("uuid");
            b.Property<string>("ResponseBodyCiphertext").IsRequired().HasColumnType("text");
            b.Property<string>("ResponseContentType").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<DateTimeOffset>("StartedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("StatusCode").HasColumnType("integer");
            b.Property<string>("Surface").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.HasKey("Id");
            b.HasIndex("RequestId").IsUnique();
            b.HasIndex("StartedAtUtc");
            b.HasIndex("Surface", "StartedAtUtc");
            b.ToTable("inference_content_logs");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.RequestAuditSummaryRecord", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            b.Property<long>("ContentLogId").HasColumnType("bigint");
            b.Property<Guid?>("DeploymentId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("GeneratedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("GeneratedBy").IsRequired().HasMaxLength(320).HasColumnType("character varying(320)");
            b.Property<string>("LogicalModel").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<Guid?>("NodeId").HasColumnType("uuid");
            b.Property<string>("SummaryCiphertext").IsRequired().HasColumnType("text");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("ContentLogId").IsUnique();
            b.HasIndex("UpdatedAtUtc");
            b.ToTable("request_audit_summaries");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.PlatformUserRecord", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("DisabledAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("DisplayName").HasMaxLength(320).HasColumnType("character varying(320)");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<DateTimeOffset?>("LastSeenAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("ObjectId").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("PrincipalName").HasMaxLength(320).HasColumnType("character varying(320)");
            b.Property<string>("ProvisioningSource").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("TenantId").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<Guid?>("UsageGroupId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("PrincipalName");
            b.HasIndex("UsageGroupId");
            b.HasIndex("TenantId", "ObjectId").IsUnique();
            b.ToTable("platform_users");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.UserAccessSettingsRecord", b =>
        {
            b.Property<int>("Id").HasColumnType("integer");
            b.Property<string>("ProvisioningMode").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.ToTable("user_access_settings");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.DailyUsageRollupRecord", b =>
        {
            b.Property<DateOnly>("DayUtc").HasColumnType("date");
            b.Property<Guid>("ApiCredentialId").HasColumnType("uuid");
            b.Property<long>("CapacityExhaustedRequests").HasColumnType("bigint");
            b.Property<long>("DurationMillisecondsTotal").HasColumnType("bigint");
            b.Property<long>("ErrorCount").HasColumnType("bigint");
            b.Property<long>("InputTokens").HasColumnType("bigint");
            b.Property<string>("LogicalModel").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<long>("OutputTokens").HasColumnType("bigint");
            b.Property<long>("RateLimitedRequests").HasColumnType("bigint");
            b.Property<long>("RequestCount").HasColumnType("bigint");
            b.Property<long>("TotalTokens").HasColumnType("bigint");
            b.Property<long>("TtftMillisecondsTotal").HasColumnType("bigint");
            b.Property<long>("TtftSampleCount").HasColumnType("bigint");
            b.Property<Guid>("UsageGroupId").HasColumnType("uuid");
            b.HasKey("DayUtc", "ApiCredentialId", "UsageGroupId", "LogicalModel");
            b.HasIndex("ApiCredentialId", "DayUtc");
            b.HasIndex("LogicalModel", "DayUtc");
            b.HasIndex("UsageGroupId", "DayUtc");
            b.ToTable("daily_usage_rollups");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.ProductUpdatePolicyRecord", b =>
        {
            b.Property<int>("Id").HasColumnType("integer");
            b.Property<int>("DayOfMonth").HasColumnType("integer");
            b.Property<int>("DayOfWeek").HasColumnType("integer");
            b.Property<DateTimeOffset?>("LastCheckedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("LastError").HasMaxLength(1200).HasColumnType("character varying(1200)");
            b.Property<DateTimeOffset?>("LastScheduledAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("LastScheduledVersion").HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<int>("LocalHour").HasColumnType("integer");
            b.Property<int>("LocalMinute").HasColumnType("integer");
            b.Property<string>("Mode").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("TimeZoneId").IsRequired().HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.ToTable("product_update_policy");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.RequestMetricRecord", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            b.Property<Guid?>("ApiCredentialId").HasColumnType("uuid");
            b.Property<int>("AttemptCount").HasColumnType("integer");
            b.Property<Guid?>("DeploymentId").HasColumnType("uuid");
            b.Property<long>("DurationMilliseconds").HasColumnType("bigint");
            b.Property<string>("ErrorCode").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<int?>("InputTokens").HasColumnType("integer");
            b.Property<bool>("IsStreaming").HasColumnType("boolean");
            b.Property<string>("LogicalModel").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<Guid?>("NodeId").HasColumnType("uuid");
            b.Property<int?>("OutputTokens").HasColumnType("integer");
            b.Property<Guid>("RequestId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("StartedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("StatusCode").HasColumnType("integer");
            b.Property<string>("Surface").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<long?>("TimeToFirstByteMilliseconds").HasColumnType("bigint");
            b.Property<int?>("TotalTokens").HasColumnType("integer");
            b.Property<long?>("UpstreamHeaderMilliseconds").HasColumnType("bigint");
            b.Property<Guid?>("UsageGroupId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("ApiCredentialId");
            b.HasIndex("RequestId").IsUnique();
            b.HasIndex("StartedAtUtc");
            b.HasIndex("LogicalModel", "StartedAtUtc");
            b.HasIndex("NodeId", "StartedAtUtc");
            b.HasIndex("UsageGroupId", "StartedAtUtc");
            b.ToTable("request_metrics");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.RuntimeStateOutboxRecord", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            b.Property<string>("Action").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<int>("AttemptCount").HasColumnType("integer");
            b.Property<Guid?>("EntityId").HasColumnType("uuid");
            b.Property<string>("Kind").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("LastError").HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<DateTimeOffset?>("NextAttemptAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("OccurredAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("PayloadJson").HasColumnType("text");
            b.Property<DateTimeOffset?>("ProcessedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("ProcessedAtUtc", "NextAttemptAtUtc", "Id");
            b.ToTable("runtime_state_outbox");
        });

        modelBuilder.Entity("LlmProxy.Domain.Deployments.ModelDeployment", b =>
        {
            b.HasOne("LlmProxy.Domain.Models.ModelDefinition", null)
                .WithMany()
                .HasForeignKey("ModelId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.HasOne("LlmProxy.Domain.Nodes.InferenceNode", null)
                .WithMany()
                .HasForeignKey("NodeId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("LlmProxy.Domain.Governance.RateLimitPolicy", b =>
        {
            b.HasOne("LlmProxy.Domain.Security.ApiCredential", null)
                .WithMany()
                .HasForeignKey("ApiCredentialId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("LlmProxy.Domain.Security.ApiCredential", b =>
        {
            b.HasOne("LlmProxy.Domain.Governance.UsageGroup", null)
                .WithMany()
                .HasForeignKey("UsageGroupId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.RequestAuditSummaryRecord", b =>
        {
            b.HasOne("LlmProxy.Infrastructure.Persistence.InferenceContentLogRecord", null)
                .WithOne("Summary")
                .HasForeignKey("LlmProxy.Infrastructure.Persistence.RequestAuditSummaryRecord", "ContentLogId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.PlatformUserRecord", b =>
        {
            b.HasOne("LlmProxy.Domain.Governance.UsageGroup", null)
                .WithMany()
                .HasForeignKey("UsageGroupId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.InferenceContentLogRecord", b =>
        {
            b.Navigation("Summary");
        });
#pragma warning restore 612, 618
    }
}
