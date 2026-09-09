using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Routing;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class RoutingTuningEndpoints
{
    public static IEndpointRouteBuilder MapRoutingTuningEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/routing/tuning");
        if (entraEnabled)
        {
            group.RequireAuthorization("AdminRead");
        }

        group.MapGet("", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var policy = await dbContext.RoutingTuningPolicies.AsNoTracking()
                .SingleAsync(item => item.Id == RoutingTuningPolicy.SingletonId, cancellationToken);
            return Results.Ok(ToResponse(policy));
        });

        var update = group.MapPut("", async (
            UpdateRoutingTuningRequest request,
            GatewayDbContext dbContext,
            RoutingTuningState tuningState,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            RoutingTuningSettings settings;
            try
            {
                settings = request.ToSettings();
                settings.Validate();
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.BadRequest(new { error = exception.ParamName, message = "Routing tuning value is outside its supported range." });
            }

            var policy = await dbContext.RoutingTuningPolicies.SingleAsync(
                item => item.Id == RoutingTuningPolicy.SingletonId,
                cancellationToken);
            var previous = policy.ToSettings();
            policy.Update(settings);

            dbContext.AuditEvents.Add(new AuditEvent(
                ResolveActor(httpContext),
                "routing.tuning.update",
                "routing_tuning_policy",
                policy.Id.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                JsonSerializer.Serialize(new { previous, current = settings })));

            await dbContext.SaveChangesAsync(cancellationToken);
            tuningState.Set(settings);
            return Results.Ok(ToResponse(policy));
        });

        if (entraEnabled)
        {
            update.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static object ToResponse(RoutingTuningPolicy policy) => new
    {
        policy.WarmupSamples,
        policy.TtftTargetMilliseconds,
        policy.TtftPenaltyWeight,
        policy.FailurePenaltyWeight,
        policy.ExternalLoadPenaltyWeight,
        policy.QueuePenaltyWeight,
        policy.KvCacheThreshold,
        policy.KvCachePenaltyWeight,
        policy.DegradedNodePenalty,
        policy.UnknownNodePenalty,
        policy.UpdatedAtUtc
    };

    private static string ResolveActor(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return "local-admin";
        }

        return httpContext.User.FindFirst("preferred_username")?.Value
            ?? httpContext.User.FindFirst(ClaimTypes.Email)?.Value
            ?? httpContext.User.Identity?.Name
            ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "authenticated-admin";
    }

    public sealed record UpdateRoutingTuningRequest(
        int WarmupSamples,
        double TtftTargetMilliseconds,
        double TtftPenaltyWeight,
        double FailurePenaltyWeight,
        double ExternalLoadPenaltyWeight,
        double QueuePenaltyWeight,
        double KvCacheThreshold,
        double KvCachePenaltyWeight,
        double DegradedNodePenalty,
        double UnknownNodePenalty)
    {
        public RoutingTuningSettings ToSettings() => new(
            WarmupSamples,
            TtftTargetMilliseconds,
            TtftPenaltyWeight,
            FailurePenaltyWeight,
            ExternalLoadPenaltyWeight,
            QueuePenaltyWeight,
            KvCacheThreshold,
            KvCachePenaltyWeight,
            DegradedNodePenalty,
            UnknownNodePenalty);
    }
}
