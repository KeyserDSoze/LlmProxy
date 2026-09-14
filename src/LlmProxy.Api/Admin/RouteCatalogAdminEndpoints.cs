using LlmProxy.Application.Abstractions;

namespace LlmProxy.Api.Admin;

public static class RouteCatalogAdminEndpoints
{
    public static IEndpointRouteBuilder MapRouteCatalogAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool entraEnabled)
    {
        var endpoint = endpoints.MapGet("/api/admin/routing/catalog", (IRouteCatalog routeCatalog) =>
        {
            var status = routeCatalog.GetStatus();
            return Results.Ok(new
            {
                provider = "in-memory",
                status.Version,
                status.NodeCount,
                status.ModelCount,
                status.DeploymentCount
            });
        });

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminRead");
        }

        return endpoints;
    }
}
