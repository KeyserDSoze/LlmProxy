using LlmProxy.Application.Abstractions;

namespace LlmProxy.Api.Admin;

public static class RuntimeStateAdminEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeStateAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var endpoint = endpoints.MapGet("/api/admin/runtime-sync", (IRuntimeStateEventSink runtimeStateSink) =>
            Results.Ok(runtimeStateSink.GetStatus()));

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminRead");
        }

        return endpoints;
    }
}
