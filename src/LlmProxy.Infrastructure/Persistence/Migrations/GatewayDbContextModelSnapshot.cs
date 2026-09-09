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

        modelBuilder.Entity("LlmProxy.Domain.Deployments.ModelDeployment", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<int?>("MaxConcurrency").HasColumnType("integer");
            b.Property<Guid>("ModelId").HasColumnType("uuid");
            b.Property<Guid>("NodeId").HasColumnType("uuid");
            b.Property<int>("Weight").HasColumnType("integer");
            b.HasKey("Id");
            b.HasIndex("ModelId");
            b.HasIndex("NodeId", "ModelId").IsUnique();
            b.ToTable("deployments");
        });

        modelBuilder.Entity("LlmProxy.Domain.Models.ModelDefinition", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<string>("ProviderModelName").IsRequired().HasMaxLength(300).HasColumnType("character varying(300)");
            b.Property<string>("PublicName").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
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
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<DateTimeOffset?>("LastHealthCheckUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("MaxConcurrency").HasColumnType("integer");
            b.Property<string>("Name").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<int>("Status").HasColumnType("integer");
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

        modelBuilder.Entity("LlmProxy.Domain.Security.ApiCredential", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<DateTimeOffset?>("ExpiresAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("KeyHash").IsRequired().HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("KeyPrefix").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset?>("LastUsedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("Name").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.HasKey("Id");
            b.HasIndex("KeyHash").IsUnique();
            b.HasIndex("KeyPrefix");
            b.ToTable("api_credentials");
        });

        modelBuilder.Entity("LlmProxy.Infrastructure.Persistence.RequestMetricRecord", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("bigint");
            b.Property<Guid?>("ApiCredentialId").HasColumnType("uuid");
            b.Property<Guid?>("DeploymentId").HasColumnType("uuid");
            b.Property<long>("DurationMilliseconds").HasColumnType("bigint");
            b.Property<string>("ErrorCode").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("LogicalModel").IsRequired().HasMaxLength(160).HasColumnType("character varying(160)");
            b.Property<Guid?>("NodeId").HasColumnType("uuid");
            b.Property<Guid>("RequestId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("StartedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<int>("StatusCode").HasColumnType("integer");
            b.HasKey("Id");
            b.HasIndex("ApiCredentialId");
            b.HasIndex("RequestId").IsUnique();
            b.HasIndex("StartedAtUtc");
            b.ToTable("request_metrics");
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
#pragma warning restore 612, 618
    }
}
