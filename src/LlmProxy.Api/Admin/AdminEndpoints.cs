using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("/overview", async (GatewayDbContext dbContext, IRequestLoadTracker tracker, CancellationToken cancellationToken) =>
        {
            var nodes = await dbContext.Nodes.AsNoTracking().OrderBy(node => node.Name).ToListAsync(cancellationToken);
            var deployments = await dbContext.Deployments.AsNoTracking().ToListAsync(cancellationToken);
            var models = await dbContext.Models.AsNoTracking().ToListAsync(cancellationToken);

            return Results.Ok(new
            {
                nodes = new
                {
                    total = nodes.Count,
                    healthy = nodes.Count(node => node.Status == NodeStatus.Healthy),
                    unhealthy = nodes.Count(node => node.Status == NodeStatus.Unhealthy),
                    draining = nodes.Count(node => node.Status == NodeStatus.Draining)
                },
                models = models.Count(model => model.Enabled),
                deployments = deployments.Count(deployment => deployment.Enabled),
                activeRequests = deployments.Sum(deployment => tracker.GetActive(deployment.Id))
            });
        });

        group.MapGet("/nodes", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await dbContext.Nodes.AsNoTracking().OrderBy(node => node.Name).ToListAsync(cancellationToken)));

        var createNode = group.MapPost("/nodes", async (CreateNodeRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var node = new InferenceNode(request.Name, request.BaseAddress, request.Weight, request.MaxConcurrency);
            dbContext.Nodes.Add(node);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/nodes/{node.Id}", node);
        });

        var drainNode = group.MapPost("/nodes/{id:guid}/drain", async (Guid id, GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.FindAsync([id], cancellationToken);
            if (node is null)
            {
                return Results.NotFound();
            }

            node.StartDrain();
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        var enableNode = group.MapPost("/nodes/{id:guid}/enable", async (Guid id, GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.FindAsync([id], cancellationToken);
            if (node is null)
            {
                return Results.NotFound();
            }

            node.Enable();
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/models", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await dbContext.Models.AsNoTracking().OrderBy(model => model.PublicName).ToListAsync(cancellationToken)));

        var createModel = group.MapPost("/models", async (CreateModelRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var model = new ModelDefinition(request.PublicName, request.ProviderModelName, request.SupportsStreaming, request.SupportsTools);
            dbContext.Models.Add(model);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/models/{model.Id}", model);
        });

        group.MapGet("/deployments", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
            Results.Ok(await dbContext.Deployments.AsNoTracking().ToListAsync(cancellationToken)));

        var createDeployment = group.MapPost("/deployments", async (CreateDeploymentRequest request, GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var nodeExists = await dbContext.Nodes.AnyAsync(node => node.Id == request.NodeId, cancellationToken);
            var modelExists = await dbContext.Models.AnyAsync(model => model.Id == request.ModelId, cancellationToken);
            if (!nodeExists || !modelExists)
            {
                return Results.BadRequest(new { error = "NodeId and ModelId must reference existing entities." });
            }

            var deployment = new ModelDeployment(request.NodeId, request.ModelId, request.Weight, request.MaxConcurrency);
            dbContext.Deployments.Add(deployment);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/admin/deployments/{deployment.Id}", deployment);
        });

        if (entraEnabled)
        {
            createNode.RequireAuthorization("AdminWrite");
            drainNode.RequireAuthorization("AdminWrite");
            enableNode.RequireAuthorization("AdminWrite");
            createModel.RequireAuthorization("AdminWrite");
            createDeployment.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    public sealed record CreateNodeRequest(string Name, string BaseAddress, int Weight = 1, int MaxConcurrency = 4);
    public sealed record CreateModelRequest(string PublicName, string ProviderModelName, bool SupportsStreaming = true, bool SupportsTools = true);
    public sealed record CreateDeploymentRequest(Guid NodeId, Guid ModelId, int Weight = 1, int? MaxConcurrency = null);
}
