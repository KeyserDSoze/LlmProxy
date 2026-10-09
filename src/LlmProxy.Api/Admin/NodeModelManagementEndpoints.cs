using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using LlmProxy.Api.AgentConnectivity;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Audit;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

public static class NodeModelManagementEndpoints
{
    public static IEndpointRouteBuilder MapNodeModelManagementEndpoints(this IEndpointRouteBuilder endpoints, bool entraEnabled)
    {
        var group = endpoints.MapGroup("/api/admin/model-management");
        if (entraEnabled) group.RequireAuthorization("AdminRead");

        group.MapGet("/catalog", () => Results.Ok(DeployableModelCatalog.All));

        group.MapGet("/nodes/{id:guid}/overview", async (
            Guid id,
            GatewayDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector protector,
            IDeploymentRuntimeMetricsTracker runtimeMetrics,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (node is null) return Results.NotFound();

            var deployments = await dbContext.Deployments.AsNoTracking()
                .Where(item => item.NodeId == id && item.ManagedInstallationId != null)
                .ToListAsync(cancellationToken);
            var models = await dbContext.Models.AsNoTracking().ToDictionaryAsync(item => item.Id, cancellationToken);

            if (string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
            {
                return Results.Ok(BuildOverview(node, null, [], deployments, models, "Management agent is not configured.", runtimeMetrics));
            }

            try
            {
                var client = httpClientFactory.CreateClient("node-management");
                var hardware = await SendAgentAsync<HardwareInventory>(
                    client, node, protector, HttpMethod.Get, "/v1/system", null, cancellationToken);
                var installed = await SendAgentAsync<ManagedModelsResponse>(
                    client, node, protector, HttpMethod.Get, "/v1/models", null, cancellationToken);

                return Results.Ok(BuildOverview(node, hardware, installed.Models ?? [], deployments, models, null, runtimeMetrics));
            }
            catch (AgentException exception)
            {
                return Results.Ok(BuildOverview(node, null, [], deployments, models, exception.Message, runtimeMetrics));
            }
        });

        var configure = group.MapPut("/nodes/{id:guid}/configuration", async (
            Guid id,
            ConfigureManagementRequest request,
            GatewayDbContext dbContext,
            UpstreamCredentialProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var node = await dbContext.Nodes.FindAsync([id], cancellationToken);
            if (node is null) return Results.NotFound();

            try
            {
                node.SetManagementBaseAddress(request.ManagementBaseAddress);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = "managementBaseAddress", message = exception.Message });
            }

            if (request.ClearBearerToken)
            {
                node.SetManagementBearerTokenCiphertext(null);
            }
            else if (!string.IsNullOrWhiteSpace(request.BearerToken))
            {
                if (!protector.IsConfigured)
                {
                    return Results.Problem(
                        "Security:UpstreamCredentialEncryptionKey must be configured before storing a management-agent bearer.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                node.SetManagementBearerTokenCiphertext(protector.Protect(request.BearerToken));
            }

            AddAudit(dbContext, httpContext, "node.management.configure", "node", node.Id.ToString(), new
            {
                node.Name,
                node.ManagementBaseAddress,
                hasManagementCredential = !string.IsNullOrWhiteSpace(node.ManagementBearerTokenCiphertext)
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                node.Id,
                node.ManagementBaseAddress,
                hasManagementCredential = !string.IsNullOrWhiteSpace(node.ManagementBearerTokenCiphertext)
            });
        });

        var install = group.MapPost("/nodes/{id:guid}/models/{catalogId}/install", async (
            Guid id,
            string catalogId,
            InstallManagedModelRequest request,
            GatewayDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var descriptor = DeployableModelCatalog.Find(catalogId);
            if (descriptor is null) return Results.NotFound(new { error = "catalog_model_not_found" });

            var node = await dbContext.Nodes.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (node is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
                return Results.BadRequest(new { error = "management_agent_not_configured" });

            try
            {
                var client = httpClientFactory.CreateClient("node-management");
                var hardware = await SendAgentAsync<HardwareInventory>(
                    client, node, protector, HttpMethod.Get, "/v1/system", null, cancellationToken);
                var compatibility = EvaluateCompatibility(descriptor, hardware);
                if (request.MaxModelLen is int requestedContext && requestedContext > descriptor.ContextTokens)
                    return Results.BadRequest(new { error = "context_exceeds_model", maxContextTokens = descriptor.ContextTokens });
                if (!request.Force && compatibility.Status == "insufficient")
                {
                    return Results.BadRequest(new
                    {
                        error = "hardware_insufficient",
                        compatibility,
                        message = "The observed free hardware does not satisfy the model's minimum planning requirements. Use force only after validating the target manually."
                    });
                }

                var installRequest = new AgentInstallRequest(
                    descriptor.Id,
                    descriptor.ProviderModelName,
                    request.Port,
                    descriptor.Runtime == "llama.cpp" ? 1 : Math.Max(1, compatibility.SuggestedTensorParallelSize),
                    request.ExtraArguments ?? [],
                    descriptor.Runtime,
                    request.MaxNumSeqs,
                    request.MaxModelLen,
                    descriptor.Runtime == "vllm" ? request.KvCacheDtype : null,
                    descriptor.Runtime == "vllm" ? request.CpuOffloadGiB : null);
                var state = await SendAgentAsync<ManagedModelState>(
                    client, node, protector, HttpMethod.Post, "/v1/models/install", installRequest, cancellationToken);

                var publicName = string.IsNullOrWhiteSpace(request.PublicName) ? descriptor.Id : request.PublicName.Trim();
                var model = await dbContext.Models.SingleOrDefaultAsync(item => item.PublicName == publicName, cancellationToken);
                if (model is null)
                {
                    model = new ModelDefinition(publicName, descriptor.ProviderModelName, descriptor.SupportsStreaming, descriptor.SupportsTools);
                    dbContext.Models.Add(model);
                }
                else if (!string.Equals(model.ProviderModelName, descriptor.ProviderModelName, StringComparison.Ordinal))
                {
                    return Results.Conflict(new { error = "logical_model_name_conflict", publicName, model.ProviderModelName });
                }

                var deployment = await dbContext.Deployments.SingleOrDefaultAsync(
                    item => item.NodeId == node.Id && item.ManagedInstallationId == state.InstallationId,
                    cancellationToken);
                if (deployment is null)
                {
                    deployment = new ModelDeployment(node.Id, model.Id);
                    dbContext.Deployments.Add(deployment);
                }
                else if (deployment.ModelId != model.Id)
                {
                    return Results.Conflict(new { error = "installation_owned_by_another_logical_model" });
                }

                var runtimeAddress = OutboundRuntimeAddress(node, state);
                deployment.ConfigureRuntime(runtimeAddress, descriptor.Id, state.InstallationId);
                if (string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase)) deployment.Enable();
                else deployment.Disable();

                AddAudit(dbContext, httpContext, "model.install", "deployment", deployment.Id.ToString(), new
                {
                    nodeId = node.Id,
                    node.Name,
                    catalogModelId = descriptor.Id,
                    descriptor.ProviderModelName,
                    publicName,
                    state.InstallationId,
                    agentStatus = state.Status,
                    state.RuntimeBaseAddress,
                    compatibilityStatus = compatibility.Status,
                    runtime = descriptor.Runtime,
                    request.MaxNumSeqs,
                    request.MaxModelLen,
                    request.KvCacheDtype,
                    request.CpuOffloadGiB
                });
                await dbContext.SaveChangesAsync(cancellationToken);

                return Results.Ok(new
                {
                    deployment,
                    model,
                    state,
                    compatibility
                });
            }
            catch (AgentException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        var start = group.MapPost("/deployments/{id:guid}/start", async (
            Guid id,
            GatewayDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var deployment = await dbContext.Deployments.FindAsync([id], cancellationToken);
            if (deployment is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(deployment.ManagedInstallationId))
                return Results.BadRequest(new { error = "deployment_not_agent_managed" });
            var node = await dbContext.Nodes.FindAsync([deployment.NodeId], cancellationToken);
            if (node is null || string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
                return Results.BadRequest(new { error = "management_agent_not_configured" });

            try
            {
                var client = httpClientFactory.CreateClient("node-management");
                var path = $"/v1/models/{Uri.EscapeDataString(deployment.ManagedInstallationId)}/start";
                var state = await SendAgentAsync<ManagedModelState>(client, node, protector, HttpMethod.Post, path, new { action = "start" }, cancellationToken);
                if (string.IsNullOrWhiteSpace(state.RuntimeBaseAddress))
                    return Results.Problem("The management agent started the model but did not return a runtimeBaseAddress.", statusCode: StatusCodes.Status502BadGateway);

                var runtimeAddress = OutboundRuntimeAddress(node, state);
                deployment.ConfigureRuntime(runtimeAddress, deployment.CatalogModelId, deployment.ManagedInstallationId);
                if (IsOutboundNode(node))
                    node.Update(node.Name, runtimeAddress!, node.Weight, node.MaxConcurrency);
                deployment.Enable();
                AddAudit(dbContext, httpContext, "model.start", "deployment", deployment.Id.ToString(), new { node.Name, deployment.ManagedInstallationId, state.RuntimeBaseAddress });
                await dbContext.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { deployment, state });
            }
            catch (AgentException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        var stop = group.MapPost("/deployments/{id:guid}/stop", async (
            Guid id,
            GatewayDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var deployment = await dbContext.Deployments.FindAsync([id], cancellationToken);
            if (deployment is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(deployment.ManagedInstallationId))
                return Results.BadRequest(new { error = "deployment_not_agent_managed" });
            var node = await dbContext.Nodes.FindAsync([deployment.NodeId], cancellationToken);
            if (node is null || string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
                return Results.BadRequest(new { error = "management_agent_not_configured" });

            // Remove it from routing before asking the remote runtime to stop.
            deployment.Disable();
            AddAudit(dbContext, httpContext, "model.stop", "deployment", deployment.Id.ToString(), new { node.Name, deployment.ManagedInstallationId });
            await dbContext.SaveChangesAsync(cancellationToken);

            try
            {
                var client = httpClientFactory.CreateClient("node-management");
                var path = $"/v1/models/{Uri.EscapeDataString(deployment.ManagedInstallationId)}/stop";
                var state = await SendAgentAsync<ManagedModelState>(client, node, protector, HttpMethod.Post, path, new { action = "stop" }, cancellationToken);
                return Results.Ok(new { deployment, state });
            }
            catch (AgentException exception)
            {
                return Results.Problem(
                    $"Routing was disabled safely, but the management agent could not confirm the stop: {exception.Message}",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        var remove = group.MapDelete("/deployments/{id:guid}", async (
            Guid id,
            GatewayDbContext dbContext,
            IHttpClientFactory httpClientFactory,
            UpstreamCredentialProtector protector,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var deployment = await dbContext.Deployments.FindAsync([id], cancellationToken);
            if (deployment is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(deployment.ManagedInstallationId))
                return Results.BadRequest(new { error = "deployment_not_agent_managed" });
            var node = await dbContext.Nodes.FindAsync([deployment.NodeId], cancellationToken);
            if (node is null || string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
                return Results.BadRequest(new { error = "management_agent_not_configured" });

            deployment.Disable();
            await dbContext.SaveChangesAsync(cancellationToken);

            try
            {
                var client = httpClientFactory.CreateClient("node-management");
                var path = $"/v1/models/{Uri.EscapeDataString(deployment.ManagedInstallationId)}";
                await SendAgentAsync<JsonElement>(client, node, protector, HttpMethod.Delete, path, null, cancellationToken);
                AddAudit(dbContext, httpContext, "model.remove", "deployment", deployment.Id.ToString(), new { node.Name, deployment.ManagedInstallationId, deployment.CatalogModelId });
                dbContext.Deployments.Remove(deployment);
                await dbContext.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            }
            catch (AgentException exception)
            {
                return Results.Problem(
                    $"Routing was disabled safely, but the management agent could not remove the model: {exception.Message}",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        if (entraEnabled)
        {
            configure.RequireAuthorization("AdminWrite");
            install.RequireAuthorization("AdminWrite");
            start.RequireAuthorization("AdminWrite");
            stop.RequireAuthorization("AdminWrite");
            remove.RequireAuthorization("AdminWrite");
        }

        return endpoints;
    }

    private static bool IsOutboundNode(InferenceNode node) =>
        Uri.TryCreate(node.ManagementBaseAddress, UriKind.Absolute, out var uri) &&
        AgentRelayHub.TryParseNode(uri, out var nodeId) && nodeId == node.Id;

    private static string? OutboundRuntimeAddress(InferenceNode node, ManagedModelState state) =>
        IsOutboundNode(node)
            ? AgentRelayHub.Root(node.Id) + "/runtime/" + state.Port
            : state.RuntimeBaseAddress;

    private static object BuildOverview(
        InferenceNode node,
        HardwareInventory? hardware,
        IReadOnlyList<ManagedModelState> agentModels,
        IReadOnlyList<ModelDeployment> deployments,
        IReadOnlyDictionary<Guid, ModelDefinition> models,
        string? agentError,
        IDeploymentRuntimeMetricsTracker runtimeMetrics) =>
        new
        {
            node = new
            {
                node.Id,
                node.Name,
                node.ManagementBaseAddress,
                hasManagementCredential = !string.IsNullOrWhiteSpace(node.ManagementBearerTokenCiphertext)
            },
            agentAvailable = hardware is not null,
            agentError,
            hardware,
            catalog = DeployableModelCatalog.All.Select(item => new
            {
                model = item,
                compatibility = EvaluateCompatibility(item, hardware)
            }),
            installations = deployments.Select(deployment =>
            {
                models.TryGetValue(deployment.ModelId, out var logicalModel);
                var agentState = agentModels.FirstOrDefault(item =>
                    string.Equals(item.InstallationId, deployment.ManagedInstallationId, StringComparison.Ordinal));
                return new
                {
                    deployment.Id,
                    deployment.ModelId,
                    logicalModel = logicalModel?.PublicName,
                    providerModelName = logicalModel?.ProviderModelName,
                    deployment.CatalogModelId,
                    deployment.ManagedInstallationId,
                    deployment.RuntimeBaseAddress,
                    deployment.Enabled,
                    agentStatus = agentState?.Status ?? (deployment.Enabled ? "unknown" : "stopped"),
                    agentState,
                    runtimeMetrics = runtimeMetrics.GetSnapshot(deployment.Id),
                    runtime = agentState?.Runtime ?? DeployableModelCatalog.Find(deployment.CatalogModelId ?? "")?.Runtime ?? "vllm"
                };
            })
        };

    private static ModelCompatibility EvaluateCompatibility(DeployableModelDescriptor model, HardwareInventory? hardware)
    {
        if (hardware is null)
            return new("unknown", "Hardware inventory unavailable.", ["Connect the management agent to calculate fit."], 1);

        var gpuCount = hardware.Gpus?.Count ?? 0;
        var freeGpu = hardware.Gpus?.Sum(gpu => gpu.MemoryFreeGiB) ?? 0;
        var totalGpu = hardware.Gpus?.Sum(gpu => gpu.MemoryTotalGiB) ?? 0;
        var reasons = new List<string>();

        if (gpuCount < model.MinimumGpuCount)
            reasons.Add($"Needs at least {model.MinimumGpuCount} GPU(s); {gpuCount} detected.");
        if (freeGpu < model.MinimumGpuMemoryGiB)
            reasons.Add($"Needs about {model.MinimumGpuMemoryGiB:0.#} GiB free aggregate GPU memory; {freeGpu:0.#} GiB is free ({totalGpu:0.#} GiB total).");
        if (hardware.SystemMemoryAvailableGiB < model.MinimumSystemMemoryGiB)
            reasons.Add($"Needs about {model.MinimumSystemMemoryGiB:0.#} GiB free system RAM; {hardware.SystemMemoryAvailableGiB:0.#} GiB is free.");
        if (hardware.DiskAvailableGiB < model.DiskGiB)
            reasons.Add($"Needs about {model.DiskGiB:0.#} GiB free disk; {hardware.DiskAvailableGiB:0.#} GiB is free.");

        var suggestedTp = Math.Max(1, Math.Min(gpuCount == 0 ? 1 : gpuCount, model.RecommendedGpuCount));
        if (reasons.Count > 0)
            return new("insufficient", "Below one or more minimum planning requirements.", reasons, suggestedTp);

        var recommended =
            freeGpu >= model.RecommendedGpuMemoryGiB &&
            hardware.SystemMemoryAvailableGiB >= model.RecommendedSystemMemoryGiB &&
            gpuCount >= model.RecommendedGpuCount;

        return recommended
            ? new("fits", "Observed free resources meet the recommended planning envelope.", [], suggestedTp)
            : new("tight", "Minimum requirements fit, but recommended headroom is not fully available.", ["Benchmark context length and concurrency before production use."], suggestedTp);
    }

    private static async Task<T> SendAgentAsync<T>(
        HttpClient client,
        InferenceNode node,
        UpstreamCredentialProtector protector,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.ManagementBaseAddress))
            throw new AgentException("Management agent is not configured.");

        using var request = new HttpRequestMessage(method, InferenceEndpoint.Combine(node.ManagementBaseAddress, path));
        protector.ApplyBearer(request, node.ManagementBearerTokenCiphertext);
        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (detail.Length > 1000) detail = detail[..1000];
            throw new AgentException($"Management agent returned HTTP {(int)response.StatusCode}: {detail}");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            return default!;

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new AgentException("Management agent returned an empty response.");
    }

    private static void AddAudit(GatewayDbContext dbContext, HttpContext httpContext, string action, string entityType, string entityId, object details)
    {
        var actor = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirst("preferred_username")?.Value
              ?? httpContext.User.FindFirst(ClaimTypes.Email)?.Value
              ?? httpContext.User.Identity?.Name
              ?? "authenticated-admin"
            : "local-admin";
        dbContext.AuditEvents.Add(new AuditEvent(
            actor,
            action,
            entityType,
            entityId,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            JsonSerializer.Serialize(details)));
    }

    public sealed record ConfigureManagementRequest(string? ManagementBaseAddress, string? BearerToken = null, bool ClearBearerToken = false);
    public sealed record InstallManagedModelRequest(string? PublicName = null, int? Port = null, bool Force = false,
        IReadOnlyList<string>? ExtraArguments = null, int? MaxNumSeqs = null, int? MaxModelLen = null,
        string? KvCacheDtype = null, double? CpuOffloadGiB = null);
    public sealed record AgentInstallRequest(string CatalogModelId, string ProviderModelName, int? Port, int TensorParallelSize,
        IReadOnlyList<string> ExtraArguments, string Runtime, int? MaxNumSeqs, int? MaxModelLen,
        string? KvCacheDtype, double? CpuOffloadGiB);
    public sealed record ModelCompatibility(string Status, string Summary, IReadOnlyList<string> Reasons, int SuggestedTensorParallelSize);
    public sealed record GpuInventory(string Name, double MemoryTotalGiB, double MemoryFreeGiB, string? DriverVersion = null, string? ComputeCapability = null);
    public sealed record HardwareInventory(
        string Hostname,
        string? OperatingSystem,
        string? Architecture,
        int CpuLogicalCores,
        double SystemMemoryTotalGiB,
        double SystemMemoryAvailableGiB,
        double DiskTotalGiB,
        double DiskAvailableGiB,
        IReadOnlyList<GpuInventory>? Gpus,
        string? Runtime,
        string? RuntimeVersion);
    public sealed record ManagedModelsResponse(IReadOnlyList<ManagedModelState>? Models);
    public sealed record ManagedModelState(
        string InstallationId,
        string? CatalogModelId,
        string? ProviderModelName,
        string Status,
        string? RuntimeBaseAddress,
        int? Port = null,
        string? Error = null,
        string Runtime = "vllm",
        int? MaxNumSeqs = null,
        int? MaxModelLen = null,
        string? KvCacheDtype = null,
        double? CpuOffloadGiB = null);
    private sealed class AgentException(string message) : Exception(message);
}
