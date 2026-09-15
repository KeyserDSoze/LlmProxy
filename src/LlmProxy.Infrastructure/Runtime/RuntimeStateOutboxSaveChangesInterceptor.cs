using System.Text.Json;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Governance;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Domain.Security;
using LlmProxy.Infrastructure.Governance;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LlmProxy.Infrastructure.Runtime;

public sealed class RuntimeStateOutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> RuntimeCredentialProperties =
    [
        nameof(ApiCredential.KeyHash),
        nameof(ApiCredential.Enabled),
        nameof(ApiCredential.ExpiresAtUtc),
        nameof(ApiCredential.UsageGroupId)
    ];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Capture(DbContext? dbContext)
    {
        if (dbContext is not GatewayDbContext gatewayDbContext)
        {
            return;
        }

        var occurredAtUtc = DateTimeOffset.UtcNow;
        var records = new List<RuntimeStateOutboxRecord>();

        Capture(
            gatewayDbContext.ChangeTracker.Entries<InferenceNode>(),
            RuntimeStateChangeKinds.Node,
            RouteNodeSnapshot.From,
            node => node.Id,
            records,
            occurredAtUtc);

        Capture(
            gatewayDbContext.ChangeTracker.Entries<ModelDefinition>(),
            RuntimeStateChangeKinds.Model,
            RouteModelSnapshot.From,
            model => model.Id,
            records,
            occurredAtUtc);

        Capture(
            gatewayDbContext.ChangeTracker.Entries<ModelDeployment>(),
            RuntimeStateChangeKinds.Deployment,
            RouteDeploymentSnapshot.From,
            deployment => deployment.Id,
            records,
            occurredAtUtc);

        foreach (var entry in gatewayDbContext.ChangeTracker.Entries<ApiCredential>())
        {
            if (entry.State == EntityState.Added ||
                entry.State == EntityState.Modified && HasRuntimeCredentialChange(entry))
            {
                records.Add(CreateUpsert(
                    RuntimeStateChangeKinds.Credential,
                    entry.Entity.Id,
                    ApiCredentialSnapshot.From(entry.Entity),
                    occurredAtUtc));
            }
            else if (entry.State == EntityState.Deleted)
            {
                records.Add(CreateRemove(RuntimeStateChangeKinds.Credential, entry.Entity.Id, occurredAtUtc));
            }
        }

        foreach (var entry in gatewayDbContext.ChangeTracker.Entries<RateLimitPolicy>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                records.Add(CreateUpsert(
                    RuntimeStateChangeKinds.RatePolicy,
                    entry.Entity.Id,
                    RateLimitPolicyRuntimeStateInterceptor.ToSnapshot(entry.Entity),
                    occurredAtUtc));
            }
            else if (entry.State == EntityState.Deleted)
            {
                records.Add(CreateRemove(RuntimeStateChangeKinds.RatePolicy, entry.Entity.Id, occurredAtUtc));
            }
        }

        if (records.Count > 0)
        {
            gatewayDbContext.RuntimeStateOutbox.AddRange(records);
        }
    }

    private static bool HasRuntimeCredentialChange(EntityEntry<ApiCredential> entry)
        => entry.Properties.Any(property =>
            property.IsModified && RuntimeCredentialProperties.Contains(property.Metadata.Name));

    private static void Capture<TEntity, TSnapshot>(
        IEnumerable<EntityEntry<TEntity>> entries,
        string kind,
        Func<TEntity, TSnapshot> toSnapshot,
        Func<TEntity, Guid> getId,
        ICollection<RuntimeStateOutboxRecord> records,
        DateTimeOffset occurredAtUtc)
        where TEntity : class
    {
        foreach (var entry in entries)
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                records.Add(CreateUpsert(kind, getId(entry.Entity), toSnapshot(entry.Entity), occurredAtUtc));
            }
            else if (entry.State == EntityState.Deleted)
            {
                records.Add(CreateRemove(kind, getId(entry.Entity), occurredAtUtc));
            }
        }
    }

    private static RuntimeStateOutboxRecord CreateUpsert<T>(
        string kind,
        Guid entityId,
        T payload,
        DateTimeOffset occurredAtUtc)
        => new()
        {
            Kind = kind,
            Action = RuntimeStateChangeKinds.Upsert,
            EntityId = entityId,
            PayloadJson = JsonSerializer.Serialize(payload),
            OccurredAtUtc = occurredAtUtc
        };

    private static RuntimeStateOutboxRecord CreateRemove(
        string kind,
        Guid entityId,
        DateTimeOffset occurredAtUtc)
        => new()
        {
            Kind = kind,
            Action = RuntimeStateChangeKinds.Remove,
            EntityId = entityId,
            OccurredAtUtc = occurredAtUtc
        };
}
