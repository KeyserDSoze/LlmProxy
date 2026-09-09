using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Infrastructure.Persistence;

public sealed class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<InferenceNode> Nodes => Set<InferenceNode>();
    public DbSet<ModelDefinition> Models => Set<ModelDefinition>();
    public DbSet<ModelDeployment> Deployments => Set<ModelDeployment>();
    public DbSet<ApiCredential> ApiCredentials => Set<ApiCredential>();
    public DbSet<RequestMetricRecord> RequestMetrics => Set<RequestMetricRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InferenceNode>(entity =>
        {
            entity.ToTable("nodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.BaseAddress).HasMaxLength(500).IsRequired();
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

        modelBuilder.Entity<RequestMetricRecord>(entity =>
        {
            entity.ToTable("request_metrics");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LogicalModel).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ErrorCode).HasMaxLength(100);
            entity.HasIndex(x => x.StartedAtUtc);
            entity.HasIndex(x => x.RequestId).IsUnique();
            entity.HasIndex(x => x.ApiCredentialId);
        });
    }
}
