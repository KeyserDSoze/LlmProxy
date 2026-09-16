namespace LlmProxy.Api.Product;

public static class ProductReleaseAdminEndpoints
{
    public static IEndpointRouteBuilder MapProductReleaseAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var endpoint = endpoints.MapGet("/api/admin/product", () => Results.Ok(ProductReleaseCatalog.GetInfo()));
        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminRead");
        }

        return endpoints;
    }
}
