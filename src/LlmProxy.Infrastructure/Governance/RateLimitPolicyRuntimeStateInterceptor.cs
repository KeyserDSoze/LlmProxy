using System.Runtime.CompilerServices;
using LlmProxy.Application.Governance;
using LlmProxy.Domain.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LlmProxy.Infrastructure.Governance;

public sealed class RateLimitPolicyRuntimeStateInterceptor(
    RequestRateLimiter rateLimiter) : SaveChangesInterceptor
{
    private sealed record PendingChanges(
        IReadOnlyList<RateLimitPolicySnapshot> Upserts,
        IReadOnlyList<Guid> Removes);

    private readonly ConditionalWeakTable<DbContext, PendingChanges> _pending = new();

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

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Clear(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Clear(eventData.Context);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? dbContext)
    {
        if (dbContext is null) return;

        var upserts = new List<RateLimitPolicySnapshot>();
        var removes = new List<Guid>();
        foreach (var entry in dbContext.ChangeTracker.Entries<RateLimitPolicy>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                upserts.Add(ToSnapshot(entry.Entity));
            }
            else if (entry.State == EntityState.Deleted)
            {
                removes.Add(entry.Entity.Id);
            }
        }

        foreach (var entry in dbContext.ChangeTracker.Entries<UserRateLimitPolicy>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                upserts.Add(ToSnapshot(entry.Entity));
            }
            else if (entry.State == EntityState.Deleted)
            {
                removes.Add(entry.Entity.Id);
            }
        }

        _pending.Remove(dbContext);
        if (upserts.Count > 0 || removes.Count > 0)
        {
            _pending.Add(dbContext, new PendingChanges(upserts, removes));
        }
    }

    private void Publish(DbContext? dbContext)
    {
        if (dbContext is null || !_pending.TryGetValue(dbContext, out var changes)) return;
        _pending.Remove(dbContext);

        foreach (var policy in changes.Upserts)
        {
            rateLimiter.UpsertPolicy(policy);
        }

        foreach (var policyId in changes.Removes)
        {
            rateLimiter.RemovePolicy(policyId);
        }
    }

    private void Clear(DbContext? dbContext)
    {
        if (dbContext is not null) _pending.Remove(dbContext);
    }

    public static RateLimitPolicySnapshot ToSnapshot(RateLimitPolicy policy) =>
        new(
            policy.Id,
            policy.ApiCredentialId,
            policy.LogicalModel,
            policy.RequestsPerWindow,
            policy.WindowSeconds,
            policy.Enabled,
            policy.OutputTokensPerWindow,
            policy.MaxOutputTokensPerRequest);

    public static RateLimitPolicySnapshot ToSnapshot(UserRateLimitPolicy policy) =>
        new(
            policy.Id,
            Guid.Empty,
            policy.LogicalModel,
            policy.RequestsPerWindow,
            policy.WindowSeconds,
            policy.Enabled,
            OwnerTenantId: policy.OwnerTenantId,
            OwnerObjectId: policy.OwnerObjectId);
}
