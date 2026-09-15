using LlmProxy.Application.Abstractions;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Runtime;

namespace LlmProxy.Api.Admin;

public static class RuntimeStateAdminEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeStateAdminEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var endpoint = endpoints.MapGet("/api/admin/runtime-sync", async (
            IRuntimeStateEventSink runtimeStateSink,
            GatewayDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var sync = runtimeStateSink.GetStatus();
            var outbox = await new RuntimeStateOutboxDiagnosticsReader(dbContext)
                .ReadAsync(cancellationToken: cancellationToken);

            return Results.Ok(new
            {
                sync.Enabled,
                sync.Provider,
                sync.InstanceId,
                sync.Connected,
                sync.LastAppliedVersion,
                sync.PublishedEvents,
                sync.ReceivedEvents,
                sync.LastError,
                Outbox = outbox
            });
        });

        if (entraEnabled)
        {
            endpoint.RequireAuthorization("AdminRead");
        }

        return endpoints;
    }
}
