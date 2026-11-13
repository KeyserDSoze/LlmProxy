namespace LlmProxy.Api.Admin;

public static class AdminOperationEndpoints
{
    public static IEndpointRouteBuilder MapAdminOperationEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/operations");
        if (entraEnabled) group.RequireAuthorization("AdminRead");

        group.MapGet("", (AdminOperationService operations) => Results.Ok(operations.List()));
        group.MapGet("/{id:guid}", (Guid id, AdminOperationService operations) =>
            operations.Get(id) is { } operation ? Results.Ok(operation) : Results.NotFound());
        return endpoints;
    }
}
