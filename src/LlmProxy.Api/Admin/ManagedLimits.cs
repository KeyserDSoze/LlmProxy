using System.Globalization;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Models;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using static LlmProxy.Api.Admin.NodeModelManagementEndpoints;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Changing what a model can hold (context, parallel requests, an engine's own memory or output setting) from Installed → Capacity.
/// A running model has to restart to use new limits, so the restart is one operation: routing is switched off while the model
/// reloads, the Agent keeps the earlier limits aside and puts them back if the new ones do not start, and routing returns only
/// after the Agent has seen the model healthy. The gateway does not enforce these numbers on requests: the engine does.
/// </summary>
internal static class ManagedLimits
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(45);
    private static readonly string[] FixedProfileRuntimes = ["faster-whisper", "laya", "kokoro", "diffusers"];

    /// <summary>Values no engine accepts at all, whatever the model.</summary>
    public static bool OutOfRange(ManagedLimitsRequest settings) =>
        settings.MaxModelLen is < 256 or > 262144 ||
        settings.MaxNumSeqs is < 1 or > 128 ||
        settings.MaxOutputTokens is < 1 or > 4096 ||
        settings.GpuMemoryUtilization is double fraction && (!double.IsFinite(fraction) || fraction < 0.1 || fraction > 0.95);

    /// <summary>Why this model cannot take these limits, or null. Runs before anything is stopped.</summary>
    public static IResult? Problem(ManagedLimitsRequest settings, ManagedModelState state, DeployableModelDescriptor? descriptor)
    {
        if (FixedProfileRuntimes.Contains(state.Runtime, StringComparer.Ordinal))
            return Results.BadRequest(new { error = "fixed_runtime_profile",
                message = "This media runtime has a fixed profile: it has no context, output or parallelism setting to change." });
        if (descriptor is not null && settings.MaxModelLen > descriptor.ContextTokens)
            return Results.BadRequest(new { error = "context_exceeds_supported_profile", maxContextTokens = descriptor.ContextTokens });
        if (state.Runtime != "vllm" && settings.GpuMemoryUtilization is not null)
            return Results.BadRequest(new { error = "gpu_memory_utilization_vllm_only" });
        if (state.Runtime != "airllm" && settings.MaxOutputTokens is not null)
            return Results.BadRequest(new { error = "output_cap_supported_by_airllm_only",
                message = "For vLLM/SGLang/llama.cpp, output tokens are controlled by each inference request within the context window." });
        if (state.Runtime == "airllm" && (settings.MaxOutputTokens ?? state.MaxOutputTokens) is int output && output > settings.MaxModelLen)
            return Results.BadRequest(new { error = "output_exceeds_context",
                message = "The maximum output cannot be larger than the context." });
        return null;
    }

    /// <summary>Whether this request restarts (or starts) the model now, rather than only saving for the next start.</summary>
    public static bool WantsRestart(ManagedLimitsRequest settings, bool running) =>
        settings.ApplyNow && (running || !settings.KeepStopped);

    /// <summary>A value the operator did not send keeps what the runtime has now.</summary>
    public static ManagedProfileUpdate Merge(ManagedLimitsRequest settings, ManagedModelState state) => new(
        settings.MaxModelLen, settings.MaxNumSeqs,
        settings.MaxOutputTokens ?? state.MaxOutputTokens,
        settings.GpuMemoryUtilization ?? state.GpuMemoryUtilization);

    /// <summary>
    /// What one request can use. llama.cpp splits its context among parallel slots; the others give each request the whole context.
    /// </summary>
    public static int EffectiveContext(string runtime, int context, int sequences) =>
        runtime == "llama.cpp" ? Math.Max(1, context / Math.Max(1, sequences)) : context;

    /// <summary>
    /// Makes the context clients read on /v1/models/details follow the runtime. It is a declaration, so it is changed only when asked,
    /// and the declared maximum output is brought down if it no longer fits.
    /// </summary>
    public static async Task SyncPublishedContextAsync(GatewayDbContext db, LlmProxy.Domain.Deployments.ModelDeployment deployment,
        string runtime, ManagedProfileUpdate profile, CancellationToken token)
    {
        var model = await db.Models.SingleOrDefaultAsync(item => item.Id == deployment.ModelId, token);
        if (model is null || model.Surface is not (ModelSurface.OpenAi or ModelSurface.Embedding)) return;
        var window = EffectiveContext(runtime, profile.MaxModelLen, profile.MaxNumSeqs);
        var output = model.MaxOutputTokens is { } declared && declared > window ? window : model.MaxOutputTokens;
        model.UpdateCapabilities(model.SupportsStreaming, model.SupportsTools, model.SupportsVision, model.SupportsReasoning, window, output);
    }

    /// <summary>The background operation behind "Apply and restart" (or "Apply and start" for a stopped model).</summary>
    public static async Task<AdminOperationOutcome> ApplyAsync(IServiceProvider services, Guid deploymentId, ManagedLimitsRequest request,
        ManagedModelOperations.Caller caller, Action<string> progress, CancellationToken token)
    {
        var db = services.GetRequiredService<GatewayDbContext>();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("node-management");
        var protector = services.GetRequiredService<UpstreamCredentialProtector>();
        var load = services.GetRequiredService<IRequestLoadTracker>();
        var deployment = await db.Deployments.SingleOrDefaultAsync(item => item.Id == deploymentId, token);
        if (deployment?.ManagedInstallationId is not { } installation) return new AdminOperationOutcome(false, "The deployment no longer exists.");
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deployment.NodeId, token);
        if (node is null || string.IsNullOrWhiteSpace(node.ManagementBaseAddress)) return new AdminOperationOutcome(false, "The server has no management Agent.");
        var logical = await db.Models.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deployment.ModelId, token);
        var name = logical?.PublicName ?? installation;

        ManagedModelState state;
        try
        {
            var inventory = await SendAgentAsync<ManagedModelsResponse>(client, node, protector, HttpMethod.Get, "/v1/models", null, token);
            state = inventory.Models?.FirstOrDefault(item => item.InstallationId == installation)
                ?? throw new AgentException("This installation no longer exists on the Agent.");
        }
        catch (AgentException error) { return new AdminOperationOutcome(false, error.Message); }

        var profile = Merge(request, state);
        var wasRunning = string.Equals(state.Status, "running", StringComparison.OrdinalIgnoreCase);
        var before = (deployment.Enabled, deployment.RoutingDesired, deployment.ActivationStartJobId);
        var jobId = Guid.NewGuid();
        // A model the operator had switched off stays off after a restart; a model that is being started is wanted back.
        var restoreRouting = !wasRunning || deployment.RoutingDesired;
        if (restoreRouting)
        {
            deployment.AwaitStart(jobId);
            await db.SaveChangesAsync(token);
        }

        if (wasRunning && before.Enabled)
        {
            try { await RuntimeEngineRestarter.WaitForDrainAsync(load, deployment.Id, name, "capacity", progress, token); }
            catch (AgentException busy)
            {
                // Nothing was sent to the Agent yet: put routing back and leave the model untouched.
                await RestoreAsync(db, deployment, before, restoreRouting, jobId);
                return new AdminOperationOutcome(false, $"{name}: the limits were not applied. {busy.Message}");
            }
        }

        ModelStartJob job;
        try
        {
            progress($"{name}: {(wasRunning ? "restarting" : "starting")} with context {Tokens(profile.MaxModelLen)} tokens, {profile.MaxNumSeqs} parallel");
            var path = $"/v1/models/{Uri.EscapeDataString(installation)}/{(wasRunning ? "restart-jobs" : "start-jobs")}";
            job = await SendAgentAsync<ModelStartJob>(client, node, protector, HttpMethod.Post, path, new { jobId, profile }, token);
            if (job.Id != jobId || job.Profile is null)
                throw new AgentException("This Agent is too old to apply limits while restarting. Upgrade the Agent, then try again.");
        }
        catch (AgentException refused)
        {
            // An error answer means no job exists: give routing back what it had. A lost connection is ambiguous, so the barrier
            // stays and the routing recovery decides once the Agent is reachable.
            if (refused.StatusCode is not null || refused.Message.StartsWith("This Agent is too old", StringComparison.Ordinal))
                await RestoreAsync(db, deployment, before, restoreRouting, jobId);
            return new AdminOperationOutcome(false, $"{name}: the limits were not applied. {refused.Message}");
        }

        try { job = await WaitForJobAsync(client, node, protector, job.Id, name, progress, token); }
        catch (AgentException lost) { return new AdminOperationOutcome(false, $"{name}: {lost.Message}. Routing recovers on its own once the model is healthy."); }

        await db.Entry(deployment).ReloadAsync(token);
        var currentNode = await db.Nodes.AsNoTracking().SingleAsync(item => item.Id == deployment.NodeId, token);
        var routable = currentNode.Enabled && currentNode.Status != LlmProxy.Domain.Nodes.NodeStatus.Draining;
        var stillOurs = deployment.RoutingDesired && deployment.ActivationStartJobId == jobId;

        if (job.Status == "completed")
        {
            if (stillOurs && routable) deployment.Enable();
            if (request.SyncPublishedContext) await SyncPublishedContextAsync(db, deployment, state.Runtime, profile, token);
            NodeModelManagementEndpoints.AddAudit(db, caller, "model.runtime.configure", "deployment", deploymentId.ToString(), new
            {
                nodeId = node.Id, installation, profile.MaxModelLen, profile.MaxNumSeqs, profile.MaxOutputTokens, profile.GpuMemoryUtilization,
                applyNow = true, restarted = wasRunning, syncedPublishedContext = request.SyncPublishedContext, outcome = "applied"
            });
            await db.SaveChangesAsync(token);
            return new AdminOperationOutcome(true,
                $"{name}: context {Tokens(profile.MaxModelLen)} tokens, {profile.MaxNumSeqs} parallel. {(stillOurs ? "Serving again." : "Running; routing stays as you left it.")}");
        }

        var cause = FirstLine(job.Error) ?? job.Detail;
        if (job.RolledBack)
        {
            if (stillOurs && routable) deployment.Enable();
            NodeModelManagementEndpoints.AddAudit(db, caller, "model.runtime.configure", "deployment", deploymentId.ToString(), new
            {
                nodeId = node.Id, installation, requested = new { profile.MaxModelLen, profile.MaxNumSeqs }, applyNow = true, outcome = "rolled_back", cause
            });
            await db.SaveChangesAsync(token);
            return new AdminOperationOutcome(false,
                $"{name}: the new limits did not fit and were not applied ({cause}). The previous limits were restored and the model is serving again.");
        }

        NodeModelManagementEndpoints.AddAudit(db, caller, "model.runtime.configure", "deployment", deploymentId.ToString(), new
        {
            nodeId = node.Id, installation, requested = new { profile.MaxModelLen, profile.MaxNumSeqs }, applyNow = true, outcome = "failed", cause
        });
        await db.SaveChangesAsync(token);
        return new AdminOperationOutcome(false, $"{name}: {cause}. Routing stays off until the model starts and is verified.");
    }

    private static async Task RestoreAsync(GatewayDbContext db, LlmProxy.Domain.Deployments.ModelDeployment deployment,
        (bool Enabled, bool RoutingDesired, Guid? ActivationStartJobId) before, bool changed, Guid jobId)
    {
        if (!changed) return;
        await db.Entry(deployment).ReloadAsync(CancellationToken.None);
        // Only undo our own barrier: an operator action taken meanwhile wins.
        if (deployment.ActivationStartJobId != jobId) return;
        deployment.RestoreRouting(before.Enabled, before.RoutingDesired, before.ActivationStartJobId);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string Tokens(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static async Task<ModelStartJob> WaitForJobAsync(HttpClient client, LlmProxy.Domain.Nodes.InferenceNode node,
        UpstreamCredentialProtector protector, Guid jobId, string name, Action<string> progress, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(RuntimeEngineRestarter.Poll, token);
            var jobs = await SendAgentAsync<IReadOnlyList<ModelStartJob>>(client, node, protector, HttpMethod.Get, "/v1/models/start-jobs", null, token);
            var current = jobs.FirstOrDefault(job => job.Id == jobId) ?? throw new AgentException("The Agent forgot the start job");
            if (current.Status is "completed" or "failed" or "cancelled" or "interrupted") return current;
            progress($"{name}: {current.Detail}");
        }
        throw new AgentException($"The model did not become healthy within {StartTimeout.TotalMinutes:0} minutes");
    }
}
