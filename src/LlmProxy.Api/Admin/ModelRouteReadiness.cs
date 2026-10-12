using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;

namespace LlmProxy.Api.Admin;

/// <summary>Safe, actionable administrative snapshot of why a logical model cannot be routed.</summary>
public static class ModelRouteReadiness
{
    public sealed record DeploymentReason(Guid DeploymentId, Guid NodeId, string NodeName,
        string NodeStatus, bool NodeEnabled, bool RoutingEnabled, bool Managed,
        string Code, string Message, string NextAction, string? LastHealthError);
    public sealed record Diagnosis(string Model, string Code, string Message,
        string NextAction, bool LogicalModelEnabled, IReadOnlyList<DeploymentReason> Deployments);

    public static Diagnosis Inspect(string publicModel, ModelDefinition? model,
        IReadOnlyList<ModelDeployment> deployments, IReadOnlyList<InferenceNode> nodes)
    {
        if (model is null)
            return new(publicModel, "model_not_registered",
                "No logical model with this name has been registered. Finish the installation first.",
                "models", false, []);
        if (!model.Enabled)
            return new(publicModel, "model_disabled",
                "The logical model is disabled. Enable it before testing.",
                "models", false, []);
        if (model.Surface != ModelSurface.OpenAi)
            return new(publicModel, "wrong_model_surface",
                "This is not a Chat model. Select the matching Playground test.",
                "models", true, []);
        if (deployments.Count == 0)
            return new(publicModel, "deployment_missing",
                "No deployment is registered. An Agent installation or a completed download is not yet a routable deployment.",
                "installations", true, []);

        var byNode = nodes.ToDictionary(n => n.Id);
        var reasons = deployments.Select(d =>
        {
            byNode.TryGetValue(d.NodeId, out var node);
            string code, message, action;
            if (node is null)
            {
                code = "node_missing"; message = "The physical server was removed."; action = "servers";
            }
            else if (!node.Enabled)
            {
                code = "node_disabled"; message = "This physical server is disabled."; action = "servers";
            }
            else if (!d.Enabled)
            {
                code = "deployment_disabled";
                message = d.ManagedInstallationId is null
                    ? "Routing is disabled on this deployment."
                    : "The model may be installed, but routing is disabled. Start it or use Check health & activate.";
                action = "installations";
            }
            else if (node.Status is NodeStatus.Unhealthy or NodeStatus.Draining or NodeStatus.Disabled)
            {
                code = "node_unavailable";
                message = "The server is not operational for routing. Check its health and connection.";
                action = "servers";
            }
            else if (d.ManagedInstallationId is not null && d.RuntimeBaseAddress is null)
            {
                code = "runtime_address_missing";
                message = "The managed runtime has no registered serving address. Start or activate it.";
                action = "installations";
            }
            else
            {
                code = "candidate_configured";
                message = "Model, deployment and server are enabled. Check route-catalog synchronization, current admission and fresh health results.";
                action = "routing";
            }
            return new DeploymentReason(d.Id, d.NodeId, node?.Name ?? d.NodeId.ToString(),
                node?.Status.ToString() ?? "Missing", node?.Enabled ?? false,
                d.Enabled, d.ManagedInstallationId is not null, code, message, action,
                node?.LastHealthError);
        }).ToArray();

        var prioritized = reasons.FirstOrDefault(x => x.Code == "deployment_disabled")
            ?? reasons.FirstOrDefault(x => x.Code == "runtime_address_missing")
            ?? reasons.FirstOrDefault(x => x.Code == "node_unavailable")
            ?? reasons.FirstOrDefault(x => x.Code == "node_disabled")
            ?? reasons.FirstOrDefault(x => x.Code != "candidate_configured")
            ?? reasons[0];

        return new(publicModel, prioritized.Code, prioritized.Message, prioritized.NextAction,
            true, reasons);
    }
}
