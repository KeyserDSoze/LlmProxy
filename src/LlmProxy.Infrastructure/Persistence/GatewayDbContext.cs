using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Governance;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;
using LlmProxy.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Persistence;

public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<InferenceNode> Nodes => Set<InferenceNode>();
    public DbSet<NodeEnrollmentRecord> NodeEnrollments => Set<NodeEnrollmentRecord>();
    public DbSet<InferenceBenchmarkJob> BenchmarkJobs => Set<InferenceBenchmarkJob>();
    public DbSet<ModelDefinition> Models => Set<ModelDefinition>();
    public DbSet<ModelDeployment> Deployments => Set<ModelDeployment>();
    public DbSet<ApiCredential> ApiCredentials => Set<ApiCredential>();
    public DbSet<UsageGroup> UsageGroups => Set<UsageGroup>();
    public DbSet<RateLimitPolicy> RateLimitPolicies => Set<RateLimitPolicy>();
    public DbSet<UserRateLimitPolicy> UserRateLimitPolicies => Set<UserRateLimitPolicy>();
    public DbSet<UsageGroupRateLimitPolicy> UsageGroupRateLimitPolicies => Set<UsageGroupRateLimitPolicy>();
    public DbSet<RoutingPolicy> RoutingPolicies => Set<RoutingPolicy>();
    public DbSet<RoutingTuningPolicy> RoutingTuningPolicies => Set<RoutingTuningPolicy>();
    public DbSet<RequestMetricRecord> RequestMetrics => Set<RequestMetricRecord>();
    public DbSet<InferenceContentLogRecord> InferenceContentLogs => Set<InferenceContentLogRecord>();
    public DbSet<RequestAuditSummaryRecord> RequestAuditSummaries => Set<RequestAuditSummaryRecord>();
    public DbSet<ContentLogSettingsRecord> ContentLogSettings => Set<ContentLogSettingsRecord>();
    public DbSet<PlatformUserRecord> PlatformUsers => Set<PlatformUserRecord>();
    public DbSet<UserAccessSettingsRecord> UserAccessSettings => Set<UserAccessSettingsRecord>();
    public DbSet<ProductUpdatePolicyRecord> ProductUpdatePolicies => Set<ProductUpdatePolicyRecord>();
    public DbSet<DailyUsageRollupRecord> DailyUsageRollups => Set<DailyUsageRollupRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RuntimeStateOutboxRecord> RuntimeStateOutbox => Set<RuntimeStateOutboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InferenceBenchmarkJob>(entity =>
        {
            entity.ToTable("inference_benchmark_jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Error).HasMaxLength(1000);
            entity.Property(x => x.ReportJson).HasColumnType("text");
            entity.HasIndex(x => new { x.DeploymentId, x.RequestedAtUtc });
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.DeploymentId).IsUnique()
                .HasDatabaseName("IX_inference_benchmark_jobs_ActiveDeployment")
                .HasFilter("\"Status\" IN ('pending', 'running')");
            entity.HasOne<LlmProxy.Domain.Deployments.ModelDeployment>().WithMany()
                .HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NodeEnrollmentRecord>(entity =>
        {
            entity.ToTable("node_enrollments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.InvitationHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.InvitationHash).IsUnique();
            entity.Property(x => x.AgentSecretHash).HasMaxLength(64);
            entity.HasIndex(x => x.AgentSecretHash).IsUnique();
            entity.HasIndex(x => x.NodeId).IsUnique();
            entity.Property(x => x.Mode).HasMaxLength(16).IsRequired();
            entity.Property(x => x.AgentVersion).HasMaxLength(80);
            entity.Property(x => x.DesiredAgentVersion).HasMaxLength(40);
        });
        modelBuilder.Entity<InferenceNode>(entity =>
        {
            entity.ToTable("nodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.BaseAddress).HasMaxLength(500).IsRequired();
            entity.Property(x => x.UpstreamBearerTokenCiphertext).HasMaxLength(4096);
            entity.Property(x => x.HardwareMetricsBaseAddress).HasMaxLength(500);
            entity.Property(x => x.ManagementBaseAddress).HasMaxLength(500);
            entity.Property(x => x.ManagementBearerTokenCiphertext).HasMaxLength(4096);
            entity.Property(x => x.LastHealthError).HasMaxLength(1000);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ModelDefinition>(entity =>
        {
            entity.ToTable("models");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PublicName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ProviderModelName).HasMaxLength(300).IsRequired();
            entity.HasIndex(x => x.PublicName).IsUnique();
        });

        modelBuilder.Entity<ModelDeployment>(entity =>
        {
            entity.ToTable("deployments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BenchmarkSource).HasMaxLength(500);
            entity.Property(x => x.RuntimeBaseAddress).HasMaxLength(500);
            entity.Property(x => x.UpstreamBearerTokenCiphertext).HasMaxLength(4096);
            entity.Property(x => x.CatalogModelId).HasMaxLength(200);
            entity.Property(x => x.ManagedInstallationId).HasMaxLength(300);
            entity.HasIndex(x => new { x.NodeId, x.ModelId }).IsUnique().HasFilter("\"ManagedInstallationId\" IS NULL");
            entity.HasIndex(x => new { x.NodeId, x.ManagedInstallationId }).IsUnique().HasFilter("\"ManagedInstallationId\" IS NOT NULL");
            entity.HasOne<InferenceNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ModelDefinition>().WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UsageGroup>(entity =>
        {
            entity.ToTable("usage_groups");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<ApiCredential>(entity =>
        {
            entity.ToTable("api_credentials");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.KeyPrefix).HasMaxLength(32).IsRequired();
            entity.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SecretCiphertext).HasMaxLength(4096);
            entity.Property(x => x.OwnerTenantId).HasMaxLength(64);
            entity.Property(x => x.OwnerObjectId).HasMaxLength(64);
            entity.Property(x => x.OwnerPrincipalName).HasMaxLength(320);
            entity.HasIndex(x => x.KeyPrefix);
            entity.HasIndex(x => x.KeyHash).IsUnique();
            entity.HasIndex(x => x.UsageGroupId);
            entity.HasIndex(x => new { x.OwnerTenantId, x.OwnerObjectId });
            entity.HasOne<UsageGroup>().WithMany().HasForeignKey(x => x.UsageGroupId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RateLimitPolicy>(entity =>
        {
            entity.ToTable("rate_limit_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LogicalModel).HasMaxLength(160);
            entity.HasIndex(x => x.ApiCredentialId);
            entity.HasIndex(x => new { x.ApiCredentialId, x.LogicalModel }).IsUnique();
            entity.HasOne<ApiCredential>().WithMany().HasForeignKey(x => x.ApiCredentialId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRateLimitPolicy>(entity =>
        {
            entity.ToTable("user_rate_limit_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnerTenantId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OwnerObjectId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LogicalModel).HasMaxLength(160);
            entity.HasIndex(x => new { x.OwnerTenantId, x.OwnerObjectId });
            entity.HasIndex(x => new { x.OwnerTenantId, x.OwnerObjectId, x.LogicalModel });
        });

        modelBuilder.Entity<UsageGroupRateLimitPolicy>(entity =>
        {
            entity.ToTable("usage_group_rate_limit_policies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LogicalModel).HasMaxLength(160);
            entity.HasIndex(x => x.UsageGroupId);
            entity.HasIndex(x => new { x.UsageGroupId, x.LogicalModel }).IsUnique();
            entity.HasOne<UsageGroup>().WithMany().HasForeignKey(x => x.UsageGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoutingPolicy>(entity =>
        {
            entity.ToTable("routing_policy");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Strategy).HasConversion<string>().HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<RoutingTuningPolicy>(entity =>
        {
            entity.ToTable("routing_tuning_policy");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<RequestMetricRecord>(entity =>
        {
            entity.ToTable("request_metrics");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LogicalModel).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Surface).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ErrorCode).HasMaxLength(100);
            entity.HasIndex(x => x.StartedAtUtc);
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasIndex(x => x.ApiCredentialId);
            entity.HasIndex(x => new { x.UsageGroupId, x.StartedAtUtc });
            entity.HasIndex(x => new { x.LogicalModel, x.StartedAtUtc });
            entity.HasIndex(x => new { x.NodeId, x.StartedAtUtc });
        });

        modelBuilder.Entity<InferenceContentLogRecord>(entity =>
        {
            entity.ToTable("inference_content_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Surface).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Method).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Path).HasMaxLength(300).IsRequired();
            entity.Property(x => x.LogicalModel).HasMaxLength(160);
            entity.Property(x => x.RequestContentType).HasMaxLength(200);
            entity.Property(x => x.ResponseContentType).HasMaxLength(200);
            entity.Property(x => x.RequestBodyCiphertext).HasColumnType("text").IsRequired();
            entity.Property(x => x.ResponseBodyCiphertext).HasColumnType("text").IsRequired();
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasIndex(x => x.StartedAtUtc);
            entity.HasIndex(x => new { x.Surface, x.StartedAtUtc });
            entity.HasOne(x => x.Summary)
                .WithOne()
                .HasForeignKey<RequestAuditSummaryRecord>(x => x.ContentLogId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RequestAuditSummaryRecord>(entity =>
        {
            entity.ToTable("request_audit_summaries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SummaryCiphertext).HasColumnType("text").IsRequired();
            entity.Property(x => x.LogicalModel).HasMaxLength(160).IsRequired();
            entity.Property(x => x.GeneratedBy).HasMaxLength(320).IsRequired();
            entity.HasIndex(x => x.ContentLogId).IsUnique();
            entity.HasIndex(x => x.UpdatedAtUtc);
        });

        modelBuilder.Entity<ContentLogSettingsRecord>(entity =>
        {
            entity.ToTable("content_log_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.SummarySystemPrompt).HasColumnType("text").IsRequired();
            entity.Property(x => x.SummaryDefaultLogicalModel).HasMaxLength(160);
        });

        modelBuilder.Entity<PlatformUserRecord>(entity =>
        {
            entity.ToTable("platform_users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ObjectId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PrincipalName).HasMaxLength(320);
            entity.Property(x => x.DisplayName).HasMaxLength(320);
            entity.Property(x => x.ProvisioningSource).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.ObjectId }).IsUnique();
            entity.HasIndex(x => x.PrincipalName);
            entity.HasIndex(x => x.UsageGroupId);
            entity.HasOne<UsageGroup>().WithMany().HasForeignKey(x => x.UsageGroupId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserAccessSettingsRecord>(entity =>
        {
            entity.ToTable("user_access_settings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.ProvisioningMode).HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<ProductUpdatePolicyRecord>(entity =>
        {
            entity.ToTable("product_update_policy");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Mode).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TimeZoneId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.LastScheduledVersion).HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(1200);
        });

        modelBuilder.Entity<DailyUsageRollupRecord>(entity =>
        {
            entity.ToTable("daily_usage_rollups");
            entity.HasKey(x => new { x.DayUtc, x.ApiCredentialId, x.UsageGroupId, x.LogicalModel });
            entity.Property(x => x.LogicalModel).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.ApiCredentialId, x.DayUtc });
            entity.HasIndex(x => new { x.UsageGroupId, x.DayUtc });
            entity.HasIndex(x => new { x.LogicalModel, x.DayUtc });
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Actor).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EntityId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.SourceIp).HasMaxLength(64);
            entity.Property(x => x.DetailsJson).HasMaxLength(4000);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        modelBuilder.Entity<RuntimeStateOutboxRecord>(entity =>
        {
            entity.ToTable("runtime_state_outbox");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(32).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.NextAttemptAtUtc, x.Id });
        });
    }
}
