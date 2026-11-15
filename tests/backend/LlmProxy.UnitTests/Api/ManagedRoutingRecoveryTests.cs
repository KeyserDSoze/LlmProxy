using System.Net;
using LlmProxy.Api.Admin;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Governance;
using LlmProxy.Domain.Models;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static LlmProxy.Api.Admin.NodeModelManagementEndpoints;

namespace LlmProxy.UnitTests.Api;

public sealed class ManagedRoutingRecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Handler : HttpMessageHandler
    {
        public bool Running = true;
        public bool Healthy = true;
        public bool HealthTransportFailure;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/health") && HealthTransportFailure) throw new HttpRequestException("Runtime disconnected");
            var body = path == "/v1/models" ? "{\"models\":[{\"installationId\":\"installed\",\"status\":\"" + (Running ? "running" : "stopped") + "\",\"port\":18000,\"runtimeBaseAddress\":\"http://agent:18000\"}]}" : "[]";
            return Task.FromResult(new HttpResponseMessage(path.EndsWith("/health") && !Healthy ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }

    [Theory]
    [InlineData(ModelSurface.OpenAi)]
    [InlineData(ModelSurface.Embedding)]
    [InlineData(ModelSurface.SpeechSynthesis)]
    [InlineData(ModelSurface.AudioTranscription)]
    [InlineData(ModelSurface.ImageGeneration)]
    [InlineData(ModelSurface.SystemOne)]
    [InlineData(ModelSurface.Realtime)]
    public async Task Restarted_gateway_restores_requested_routing_without_any_browser(ModelSurface surface)
    {
        var handler = new Handler();
        using var services = new ServiceCollection()
            .AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()))
            .AddSingleton<IHttpClientFactory>(new Factory(handler))
            .AddSingleton(new UpstreamCredentialProtector(new ConfigurationBuilder().Build())).BuildServiceProvider();
        var db = services.GetRequiredService<GatewayDbContext>();
        var model = new ModelDefinition("my-model", "org/model", surface: surface);
        var node = new InferenceNode("server", "http://agent:18000");
        node.SetManagementBaseAddress("http://agent:9900");
        var deployment = new ModelDeployment(node.Id, model.Id);
        deployment.ConfigureRuntime("http://agent:18000", "custom", "installed");
        deployment.SuspendRouting();
        db.Models.Add(model); db.Nodes.Add(node); db.Deployments.Add(deployment); await db.SaveChangesAsync(Token);
        db.ChangeTracker.Clear(); // Simulates reconstructing the state from PostgreSQL after a gateway restart.

        await ManagedRoutingRecoveryWorker.ReconcileNodeAsync(services, node.Id, Token);
        Assert.True((await db.Deployments.SingleAsync(Token)).Enabled);
        handler.HealthTransportFailure = true;
        await ManagedRoutingRecoveryWorker.ReconcileNodeAsync(services, node.Id, Token);
        Assert.False((await db.Deployments.SingleAsync(Token)).Enabled);
        handler.HealthTransportFailure = false;
        handler.Running = false;
        await ManagedRoutingRecoveryWorker.ReconcileNodeAsync(services, node.Id, Token);
        Assert.False((await db.Deployments.SingleAsync(Token)).Enabled);
        Assert.True((await db.Deployments.SingleAsync(Token)).RoutingDesired);
        handler.Running = true; handler.Healthy = false;
        await ManagedRoutingRecoveryWorker.ReconcileNodeAsync(services, node.Id, Token);
        Assert.False((await db.Deployments.SingleAsync(Token)).Enabled);
        handler.Healthy = true;
        await ManagedRoutingRecoveryWorker.ReconcileNodeAsync(services, node.Id, Token);
        Assert.True((await db.Deployments.SingleAsync(Token)).Enabled);
        (await db.Deployments.SingleAsync(Token)).Disable(); await db.SaveChangesAsync(Token);
        await ManagedRoutingRecoveryWorker.ReconcileNodeAsync(services, node.Id, Token);
        Assert.False((await db.Deployments.SingleAsync(Token)).Enabled); // An explicit Stop is never undone.
    }

    [Fact]
    public void A_restart_waits_for_a_new_start_not_a_historical_success()
    {
        var deployment = new ModelDeployment(Guid.NewGuid(), Guid.NewGuid());
        deployment.ConfigureRuntime("http://agent:18000", "custom", "installed");
        var now = DateTimeOffset.UtcNow;
        var requestedId = Guid.NewGuid();
        deployment.AwaitStart(requestedId);
        var state = new ManagedModelState("installed", "custom", "org/model", "running", "http://agent:18000", 18000);
        ModelStartJob Job(DateTimeOffset started, Guid? id = null, string status = "completed") => new(id ?? Guid.NewGuid(), "installed", status, "ok", null, state, started, started.AddSeconds(1));
        Assert.False(ManagedRoutingRecoveryWorker.CanRecover(deployment, state, [Job(now.AddDays(-1))]));
        Assert.False(ManagedRoutingRecoveryWorker.CanRecover(deployment, state, [Job(now.AddDays(1))]));
        Assert.False(ManagedRoutingRecoveryWorker.CanRecover(deployment, state, [Job(now, requestedId, "starting"), Job(now.AddDays(-1))]));
        Assert.True(ManagedRoutingRecoveryWorker.CanRecover(deployment, state, [Job(now.AddDays(-1), requestedId)])); // Remote clock skew is irrelevant.
        Assert.True(ManagedRoutingRecoveryWorker.CanRecover(deployment, state, [Job(now, requestedId, "interrupted"), Job(now.AddMinutes(1))]));
        Assert.False(ManagedRoutingRecoveryWorker.CanRecover(deployment, state, [Job(now, requestedId, "cancelled"), Job(now.AddMinutes(1))]));
        Assert.False(ManagedRoutingRecoveryWorker.CanRecover(deployment, state with { DesiredRunning = false }, [Job(now, requestedId)]));
        deployment.Disable();
        Assert.False(ManagedRoutingRecoveryWorker.CanRecover(deployment, state, [Job(now, requestedId)]));
    }

    [Fact]
    public async Task Rename_keeps_identity_capabilities_deployments_and_quota_ids()
    {
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var model = new ModelDefinition("old-name", "org/model");
        model.UpdateCapabilities(true, true, true, true, 32768, 8192);
        var deployment = new ModelDeployment(Guid.NewGuid(), model.Id);
        var policy = new RateLimitPolicy(Guid.NewGuid(), "old-name", 10, 60, outputTokensPerWindow: 100, maxOutputTokensPerRequest: 10);
        var userPolicy = new UserRateLimitPolicy("tenant", "owner", "old-name", 20, outputTokensPerWindow: 200, maxOutputTokensPerRequest: 20);
        var groupPolicy = new UsageGroupRateLimitPolicy(Guid.NewGuid(), "old-name", 30, outputTokensPerWindow: 300, maxOutputTokensPerRequest: 30);
        db.UserRateLimitPolicies.Add(userPolicy); db.UsageGroupRateLimitPolicies.Add(groupPolicy);
        db.Models.Add(model); db.Deployments.Add(deployment); db.RateLimitPolicies.Add(policy); await db.SaveChangesAsync(Token);
        var policyId = policy.Id;
        model.Rename("new-name");
        await ModelPublishingEndpoints.MovePoliciesAsync(db, "old-name", "new-name", Token);
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        Assert.Equal(model.Id, (await db.Models.SingleAsync(Token)).Id);
        Assert.Equal("org/model", (await db.Models.SingleAsync(Token)).ProviderModelName);
        Assert.True((await db.Models.SingleAsync(Token)).SupportsVision);
        Assert.Equal(model.Id, (await db.Deployments.SingleAsync(Token)).ModelId);
        Assert.Equal(policyId, (await db.RateLimitPolicies.SingleAsync(Token)).Id);
        Assert.Equal("new-name", (await db.RateLimitPolicies.SingleAsync(Token)).LogicalModel);
        Assert.Equal(100, (await db.RateLimitPolicies.SingleAsync(Token)).OutputTokensPerWindow);
        Assert.Equal(userPolicy.Id, (await db.UserRateLimitPolicies.SingleAsync(Token)).Id);
        Assert.Equal("new-name", (await db.UserRateLimitPolicies.SingleAsync(Token)).LogicalModel);
        Assert.Equal(200, (await db.UserRateLimitPolicies.SingleAsync(Token)).OutputTokensPerWindow);
        Assert.Equal(groupPolicy.Id, (await db.UsageGroupRateLimitPolicies.SingleAsync(Token)).Id);
        Assert.Equal("new-name", (await db.UsageGroupRateLimitPolicies.SingleAsync(Token)).LogicalModel);
        Assert.Equal(300, (await db.UsageGroupRateLimitPolicies.SingleAsync(Token)).OutputTokensPerWindow);
    }

    [Fact]
    public async Task Replaying_an_install_after_rename_does_not_replace_identity_or_undo_a_disable()
    {
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var descriptor = DeployableModelCatalog.Find("qwen3-8b")!;
        var model = new ModelDefinition("renamed-model", descriptor.ProviderModelName);
        var node = new InferenceNode("server", "http://agent:18000");
        var deployment = new ModelDeployment(node.Id, model.Id);
        deployment.ConfigureRuntime("http://agent:18000", descriptor.Id, "installed");
        deployment.Disable();
        db.Models.Add(model); db.Nodes.Add(node); db.Deployments.Add(deployment); await db.SaveChangesAsync(Token);
        var result = await NodeModelManagementEndpoints.RegisterManagedInstallationAsync(
            new ManagedModelState("installed", descriptor.Id, descriptor.ProviderModelName, "running", "http://agent:18000", 18000),
            new InstallManagedModelRequest(PublicName: "old-job-name"), descriptor, new ModelCompatibility("unknown", "test", [], 1),
            node, db, new DefaultHttpContext(), Token);
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Single(await db.Models.ToListAsync(Token));
        Assert.Equal("renamed-model", (await db.Models.SingleAsync(Token)).PublicName);
        Assert.Equal(model.Id, (await db.Deployments.SingleAsync(Token)).ModelId);
        Assert.False((await db.Deployments.SingleAsync(Token)).RoutingDesired);
        Assert.False((await db.Deployments.SingleAsync(Token)).Enabled);
    }

    [Fact]
    public async Task Rename_does_not_merge_preexisting_user_policy_scopes()
    {
        using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.UserRateLimitPolicies.AddRange(new UserRateLimitPolicy("tenant", "owner", "old-name", 10), new UserRateLimitPolicy("tenant", "owner", "new-name", 20));
        await db.SaveChangesAsync(Token);
        Assert.False(await ModelPublishingEndpoints.MovePoliciesAsync(db, "old-name", "new-name", Token));
        Assert.Equal(1, await db.UserRateLimitPolicies.CountAsync(item => item.LogicalModel == "old-name", Token));
        Assert.Equal(1, await db.UserRateLimitPolicies.CountAsync(item => item.LogicalModel == "new-name", Token));
    }
}
