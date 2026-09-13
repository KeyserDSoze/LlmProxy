using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Routing;
using LlmProxy.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Persistence;

public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<InferenceNode> Nodes => Set<InferenceNode>();
    public DbSet<ModelDefinition> Models => Set<ModelDefinition>();
    public DbSet<ModelDeployment> Deployments => Set<ModelDeployment>();
    public DbSet<ApiCredential> ApiCredentials => Set<ApiCredential>();
    public DbSet<RoutingPolicy> RoutingPolicies => Set<RoutingPolicy>();
    public DbSet<RoutingTuningPolicy> RoutingTuningPolicies => Set<RoutingTuningPolicy>();
    public DbSet<RequestMetricRecord> RequestMetrics => Set<RequestMetricRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InferenceNode>(entity =>
        {
            entity.ToTable("nodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.BaseAddress).HasMaxLength(500).IsRequired();
            entity.Property(x => x.HardwareMetricsBaseAddress).HasMaxLength(500);
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
            entity.HasIndex(x => new { x.NodeId, x.ModelId }).IsUnique();
            entity.HasOne<InferenceNode>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ModelDefinition>().WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiCredential>(entity =>
        {
            entity.ToTable("api_credentials");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.KeyPrefix).HasMaxLength(32).IsRequired();
            entity.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.KeyPrefix);
            entity.HasIndex(x => x.KeyHash).IsUnique();
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
            entity.HasIndex(x => new { x.LogicalModel, x.StartedAtUtc });
            entity.HasIndex(x => new { x.NodeId, x.StartedAtUtc });
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
    }
}
