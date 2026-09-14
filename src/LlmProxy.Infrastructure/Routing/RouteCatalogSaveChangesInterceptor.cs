using System.Runtime.CompilerServices;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LlmProxy.Infrastructure.Routing;

public sealed class RouteCatalogSaveChangesInterceptor(
    IRouteCatalog routeCatalog,
    IRuntimeStateEventSink runtimeStateSink) : SaveChangesInterceptor
{
    private sealed record PendingChanges(
        IReadOnlyList<RouteNodeSnapshot> NodeUpserts,
        IReadOnlyList<Guid> NodeRemoves,
        IReadOnlyList<RouteModelSnapshot> ModelUpserts,
        IReadOnlyList<Guid> ModelRemoves,
        IReadOnlyList<RouteDeploymentSnapshot> DeploymentUpserts,
        IReadOnlyList<Guid> DeploymentRemoves);

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

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
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

        var nodeUpserts = new List<RouteNodeSnapshot>();
        var nodeRemoves = new List<Guid>();
        Capture(dbContext.ChangeTracker.Entries<InferenceNode>(), RouteNodeSnapshot.From, node => node.Id, nodeUpserts, nodeRemoves);

        var modelUpserts = new List<RouteModelSnapshot>();
        var modelRemoves = new List<Guid>();
        Capture(dbContext.ChangeTracker.Entries<ModelDefinition>(), RouteModelSnapshot.From, model => model.Id, modelUpserts, modelRemoves);

        var deploymentUpserts = new List<RouteDeploymentSnapshot>();
        var deploymentRemoves = new List<Guid>();
        Capture(dbContext.ChangeTracker.Entries<ModelDeployment>(), RouteDeploymentSnapshot.From, deployment => deployment.Id, deploymentUpserts, deploymentRemoves);

        _pending.Remove(dbContext);
        if (nodeUpserts.Count > 0 || nodeRemoves.Count > 0 || modelUpserts.Count > 0 || modelRemoves.Count > 0 || deploymentUpserts.Count > 0 || deploymentRemoves.Count > 0)
        {
            _pending.Add(dbContext, new PendingChanges(nodeUpserts, nodeRemoves, modelUpserts, modelRemoves, deploymentUpserts, deploymentRemoves));
        }
    }

    private void Publish(DbContext? dbContext)
    {
        if (dbContext is null || !_pending.TryGetValue(dbContext, out var changes)) return;
        _pending.Remove(dbContext);

        foreach (var node in changes.NodeUpserts) { routeCatalog.Upsert(node); runtimeStateSink.PublishNodeUpsert(node); }
        foreach (var nodeId in changes.NodeRemoves) { routeCatalog.RemoveNode(nodeId); runtimeStateSink.PublishNodeRemove(nodeId); }
        foreach (var model in changes.ModelUpserts) { routeCatalog.Upsert(model); runtimeStateSink.PublishModelUpsert(model); }
        foreach (var modelId in changes.ModelRemoves) { routeCatalog.RemoveModel(modelId); runtimeStateSink.PublishModelRemove(modelId); }
        foreach (var deployment in changes.DeploymentUpserts) { routeCatalog.Upsert(deployment); runtimeStateSink.PublishDeploymentUpsert(deployment); }
        foreach (var deploymentId in changes.DeploymentRemoves) { routeCatalog.RemoveDeployment(deploymentId); runtimeStateSink.PublishDeploymentRemove(deploymentId); }
    }

    private void Clear(DbContext? dbContext)
    {
        if (dbContext is not null) _pending.Remove(dbContext);
    }

    private static void Capture<TEntity, TSnapshot>(
        IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity>> entries,
        Func<TEntity, TSnapshot> toSnapshot,
        Func<TEntity, Guid> getId,
        ICollection<TSnapshot> upserts,
        ICollection<Guid> removes)
        where TEntity : class
    {
        foreach (var entry in entries)
        {
            if (entry.State is EntityState.Added or EntityState.Modified) upserts.Add(toSnapshot(entry.Entity));
            else if (entry.State == EntityState.Deleted) removes.Add(getId(entry.Entity));
        }
    }
}
