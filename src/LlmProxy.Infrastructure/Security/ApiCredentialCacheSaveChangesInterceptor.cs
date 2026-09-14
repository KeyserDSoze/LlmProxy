using System.Runtime.CompilerServices;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LlmProxy.Infrastructure.Security;

public sealed class ApiCredentialCacheSaveChangesInterceptor(IApiCredentialCache credentialCache) : SaveChangesInterceptor
{
    private sealed record PendingChanges(
        IReadOnlyList<ApiCredentialSnapshot> Upserts,
        IReadOnlyList<Guid> Removes);

    private readonly ConditionalWeakTable<DbContext, PendingChanges> _pending = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
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

    private void Capture(DbContext? dbContext)
    {
        if (dbContext is null)
        {
            return;
        }

        var upserts = new List<ApiCredentialSnapshot>();
        var removes = new List<Guid>();
        foreach (var entry in dbContext.ChangeTracker.Entries<ApiCredential>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                case EntityState.Modified:
                    upserts.Add(ApiCredentialSnapshot.From(entry.Entity));
                    break;
                case EntityState.Deleted:
                    removes.Add(entry.Entity.Id);
                    break;
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
        if (dbContext is null || !_pending.TryGetValue(dbContext, out var changes))
        {
            return;
        }

        _pending.Remove(dbContext);
        foreach (var credential in changes.Upserts)
        {
            credentialCache.Upsert(credential);
        }

        foreach (var credentialId in changes.Removes)
        {
            credentialCache.Remove(credentialId);
        }
    }
}
