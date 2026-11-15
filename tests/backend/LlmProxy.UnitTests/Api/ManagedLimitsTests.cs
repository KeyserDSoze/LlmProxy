using System.Net;
using System.Text;
using System.Text.Json;
using LlmProxy.Api.Admin;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Deployments;
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

public sealed class ManagedLimitsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>An Agent whose model is <c>a</c>; how it answers the restart/start job is chosen by <c>Mode</c>.</summary>
    private sealed class FakeAgent(string status, string runtime = "llama.cpp", int context = 32768, int sequences = 1, double? gpu = null) : HttpMessageHandler
    {
        public string Mode = "completed";
        public List<string> Calls { get; } = [];
        public JsonElement? Posted { get; private set; }
        public Action? OnJobPosted;
        private Guid _jobId;
        private string _path = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method + " " + path);
            if (path == "/v1/models")
                return Reply("{\"models\":[{\"installationId\":\"a\",\"catalogModelId\":\"custom:" + runtime + "\",\"providerModelName\":\"org/a\",\"status\":\"" + status +
                    "\",\"runtime\":\"" + runtime + "\",\"port\":18000,\"maxModelLen\":" + context + ",\"maxNumSeqs\":" + sequences +
                    (gpu is null ? "" : ",\"gpuMemoryUtilization\":" + gpu.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                    ",\"runtimeBaseAddress\":\"http://agent:18000\"}]}");
            if (request.Method == HttpMethod.Post && (path.EndsWith("/restart-jobs") || path.EndsWith("/start-jobs")))
            {
                OnJobPosted?.Invoke();
                if (Mode == "refused") return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":\"A runtime start is already in progress.\"}") };
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Posted = body.RootElement.Clone();
                _path = path;
                _jobId = body.RootElement.GetProperty("jobId").GetGuid();
                var shown = Mode == "old_agent" ? Guid.NewGuid() : _jobId;
                return Reply(Job(shown, "queued", Mode != "old_agent", false, null));
            }
            if (path == "/v1/models/start-jobs")
            {
                var (final, error, rolled) = Mode switch
                {
                    "rolled_back" => ("failed", "CUDA out of memory\nmore text", true),
                    "failed" => ("failed", "Runtime did not become healthy", false),
                    _ => ("completed", null, false)
                };
                return Reply("[" + Job(_jobId, final, true, rolled, error) + "]");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private string Job(Guid id, string jobStatus, bool withProfile, bool rolledBack, string? error)
        {
            var profile = withProfile && Posted is { } posted ? posted.GetProperty("profile").GetRawText() : "null";
            return "{\"id\":\"" + id + "\",\"installationId\":\"a\",\"status\":\"" + jobStatus + "\",\"detail\":\"" + (error is null ? "ok" : "failed") +
                "\",\"error\":" + (error is null ? "null" : JsonSerializer.Serialize(error)) + ",\"createdAtUtc\":\"2026-11-15T10:00:00Z\",\"restart\":" +
                (_path.EndsWith("/restart-jobs") ? "true" : "false") + ",\"profile\":" + profile + ",\"rolledBack\":" + (rolledBack ? "true" : "false") + "}";
        }

        private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Load : IRequestLoadTracker
    {
        public int GetActive(Guid deploymentId) => 0;
        public IDisposable Enter(Guid deploymentId) => throw new NotSupportedException();
        public bool TryEnter(Guid deploymentId, Guid nodeId, int deploymentMaxConcurrency, int nodeMaxConcurrency, out IDisposable? lease) => throw new NotSupportedException();
    }

    private static (ServiceProvider Services, Guid DeploymentId) Arrange(FakeAgent agent, bool routed = true, bool desired = true)
    {
        RuntimeEngineRestarter.Poll = TimeSpan.FromMilliseconds(10);
        var services = new ServiceCollection()
            .AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")))
            .AddSingleton<IHttpClientFactory>(new Factory(agent))
            .AddSingleton(new UpstreamCredentialProtector(new ConfigurationBuilder().Build()))
            .AddSingleton<IRequestLoadTracker>(new Load())
            .BuildServiceProvider();
        var db = services.GetRequiredService<GatewayDbContext>();
        var model = new ModelDefinition("my-model", "org/a");
        model.UpdateCapabilities(true, true, false, false, 32768, 8192);
        var node = new InferenceNode("spark", "http://agent.test:8000");
        node.SetManagementBaseAddress("http://agent.test:9900");
        var deployment = new ModelDeployment(node.Id, model.Id);
        deployment.ConfigureRuntime("http://agent.test:18000", "custom:llama.cpp", "a");
        if (!desired) deployment.Disable();
        else if (!routed) deployment.SuspendRouting();
        db.Models.Add(model); db.Nodes.Add(node); db.Deployments.Add(deployment);
        db.SaveChanges();
        return (services, deployment.Id);
    }

    private static ManagedLimitsRequest Request(int context = 65536, int sequences = 1, bool sync = false, int? output = null, double? gpu = null) =>
        new(context, sequences, output, gpu, ApplyNow: true, SyncPublishedContext: sync);

    private static Task<AdminOperationOutcome> Apply(ServiceProvider services, Guid id, ManagedLimitsRequest request) =>
        ManagedLimits.ApplyAsync(services, id, request, new ManagedModelOperations.Caller("tester", null), _ => { }, Token);

    private static ModelDeployment Current(ServiceProvider services, Guid id) =>
        services.GetRequiredService<GatewayDbContext>().Deployments.AsNoTracking().Single(item => item.Id == id);

    [Fact]
    public async Task A_running_model_is_restarted_with_the_new_limits_and_routed_again_only_when_healthy()
    {
        var agent = new FakeAgent("running");
        var (services, id) = Arrange(agent);
        // While the Agent restarts the model, routing must already be off and waiting for exactly this job.
        agent.OnJobPosted = () =>
        {
            var during = Current(services, id);
            Assert.False(during.Enabled);
            Assert.True(during.RoutingDesired);
            Assert.NotNull(during.ActivationStartJobId);
        };

        var outcome = await Apply(services, id, Request(context: 65536, sync: true));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("context 65,536 tokens", outcome.Message);
        Assert.Contains("Serving again", outcome.Message);
        Assert.Contains("POST /v1/models/a/restart-jobs", agent.Calls);
        Assert.DoesNotContain("POST /v1/models/a/start-jobs", agent.Calls);
        Assert.Equal(65536, agent.Posted!.Value.GetProperty("profile").GetProperty("maxModelLen").GetInt32());
        var deployment = Current(services, id);
        Assert.True(deployment.Enabled);
        Assert.Null(deployment.ActivationStartJobId);
        var db = services.GetRequiredService<GatewayDbContext>();
        Assert.Equal(65536, (await db.Models.AsNoTracking().SingleAsync(Token)).ContextWindowTokens);
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(Token);
        Assert.Equal("model.runtime.configure", audit.Action);
        Assert.Contains("applied", audit.DetailsJson);
    }

    [Fact]
    public async Task The_published_context_is_left_alone_unless_it_was_asked_to_follow()
    {
        var agent = new FakeAgent("running");
        var (services, id) = Arrange(agent);

        await Apply(services, id, Request(sync: false));

        Assert.Equal(32768, (await services.GetRequiredService<GatewayDbContext>().Models.AsNoTracking().SingleAsync(Token)).ContextWindowTokens);
    }

    [Fact]
    public async Task Limits_that_do_not_start_are_rolled_back_and_the_model_serves_again_with_the_old_ones()
    {
        var agent = new FakeAgent("running") { Mode = "rolled_back" };
        var (services, id) = Arrange(agent);

        var outcome = await Apply(services, id, Request(context: 262144, sync: true));

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not fit", outcome.Message);
        Assert.Contains("CUDA out of memory", outcome.Message);
        Assert.Contains("previous limits were restored", outcome.Message);
        Assert.DoesNotContain("more text", outcome.Message);
        Assert.True(Current(services, id).Enabled);
        // The clients keep reading what the model really serves.
        Assert.Equal(32768, (await services.GetRequiredService<GatewayDbContext>().Models.AsNoTracking().SingleAsync(Token)).ContextWindowTokens);
        Assert.Contains("rolled_back", (await services.GetRequiredService<GatewayDbContext>().AuditEvents.AsNoTracking().SingleAsync(Token)).DetailsJson);
    }

    [Fact]
    public async Task When_nothing_runs_afterwards_routing_stays_off_and_the_intent_is_kept_for_recovery()
    {
        var agent = new FakeAgent("running") { Mode = "failed" };
        var (services, id) = Arrange(agent);

        var outcome = await Apply(services, id, Request());

        Assert.False(outcome.Succeeded);
        Assert.Contains("Routing stays off", outcome.Message);
        var deployment = Current(services, id);
        Assert.False(deployment.Enabled);
        Assert.True(deployment.RoutingDesired);
    }

    [Fact]
    public async Task An_agent_that_refuses_the_job_gets_routing_back_exactly_as_it_was()
    {
        var agent = new FakeAgent("running") { Mode = "refused" };
        var (services, id) = Arrange(agent);

        var outcome = await Apply(services, id, Request());

        Assert.False(outcome.Succeeded);
        Assert.Contains("limits were not applied", outcome.Message);
        var deployment = Current(services, id);
        Assert.True(deployment.Enabled);
        Assert.Null(deployment.ActivationStartJobId);
    }

    [Fact]
    public async Task An_agent_that_cannot_apply_limits_while_restarting_is_reported_and_routing_is_restored()
    {
        var agent = new FakeAgent("running") { Mode = "old_agent" };
        var (services, id) = Arrange(agent);

        var outcome = await Apply(services, id, Request());

        Assert.False(outcome.Succeeded);
        Assert.Contains("too old", outcome.Message);
        Assert.True(Current(services, id).Enabled);
    }

    [Fact]
    public async Task A_model_the_operator_switched_off_is_restarted_but_stays_off()
    {
        var agent = new FakeAgent("running");
        var (services, id) = Arrange(agent, desired: false);

        var outcome = await Apply(services, id, Request());

        Assert.True(outcome.Succeeded, outcome.Message);
        var deployment = Current(services, id);
        Assert.False(deployment.Enabled);
        Assert.False(deployment.RoutingDesired);
        Assert.Null(deployment.ActivationStartJobId);
    }

    [Fact]
    public async Task A_stopped_model_is_started_with_the_new_limits_and_wanted_in_routing()
    {
        var agent = new FakeAgent("stopped");
        var (services, id) = Arrange(agent, desired: false);

        var outcome = await Apply(services, id, Request(context: 131072));

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains("POST /v1/models/a/start-jobs", agent.Calls);
        Assert.DoesNotContain("POST /v1/models/a/restart-jobs", agent.Calls);
        var deployment = Current(services, id);
        Assert.True(deployment.Enabled);
        Assert.True(deployment.RoutingDesired);
    }

    [Fact]
    public async Task A_missing_engine_setting_keeps_the_one_the_runtime_has()
    {
        var state = new ManagedModelState("a", "custom:vllm", "org/a", "stopped", null, Runtime: "vllm", MaxOutputTokens: null, GpuMemoryUtilization: 0.6);

        var merged = ManagedLimits.Merge(new ManagedLimitsRequest(65536, 2, null), state);

        Assert.Equal(new ManagedProfileUpdate(65536, 2, null, 0.6), merged);
        Assert.Equal(0.8, ManagedLimits.Merge(new ManagedLimitsRequest(65536, 2, null, 0.8), state).GpuMemoryUtilization);
    }

    [Theory]
    [InlineData("llama.cpp", 65536, 2, 32768)]   // the slots share the pool
    [InlineData("llama.cpp", 65536, 1, 65536)]
    [InlineData("vllm", 65536, 4, 65536)]        // each request may use the whole context
    [InlineData("sglang", 32768, 8, 32768)]
    public void One_request_can_use_the_pool_divided_by_the_slots_only_on_llama_cpp(string runtime, int context, int sequences, int expected) =>
        Assert.Equal(expected, ManagedLimits.EffectiveContext(runtime, context, sequences));

    [Fact]
    public void Limits_a_model_cannot_take_are_refused_before_anything_is_stopped()
    {
        ManagedModelState State(string runtime, int? output = null) => new("a", "custom", "org/a", "running", null, Runtime: runtime, MaxOutputTokens: output);
        static int Code(IResult? result) => result is IStatusCodeHttpResult { StatusCode: { } code } ? code : 0;

        Assert.Equal(400, Code(ManagedLimits.Problem(new ManagedLimitsRequest(8192, 1, null), State("kokoro"), null)));
        Assert.Equal(400, Code(ManagedLimits.Problem(new ManagedLimitsRequest(8192, 1, null, 0.7), State("llama.cpp"), null)));
        Assert.Equal(400, Code(ManagedLimits.Problem(new ManagedLimitsRequest(8192, 1, 256), State("vllm"), null)));
        Assert.Equal(400, Code(ManagedLimits.Problem(new ManagedLimitsRequest(2048, 1, 4096), State("airllm"), null)));
        Assert.Equal(400, Code(ManagedLimits.Problem(new ManagedLimitsRequest(2048, 1, null), State("airllm", output: 4096), null)));
        Assert.Null(ManagedLimits.Problem(new ManagedLimitsRequest(65536, 2, null), State("llama.cpp"), null));
        Assert.Null(ManagedLimits.Problem(new ManagedLimitsRequest(8192, 1, 512), State("airllm"), null));
        Assert.Null(ManagedLimits.Problem(new ManagedLimitsRequest(8192, 1, null, 0.7), State("vllm"), null));
    }

    [Theory]
    [InlineData(255, 1, null, null)]
    [InlineData(262145, 1, null, null)]
    [InlineData(8192, 0, null, null)]
    [InlineData(8192, 129, null, null)]
    [InlineData(8192, 1, 0, null)]
    [InlineData(8192, 1, 4097, null)]
    [InlineData(8192, 1, null, 0.05)]
    [InlineData(8192, 1, null, 0.99)]
    [InlineData(8192, 1, null, double.NaN)]
    public void Values_outside_every_engine_range_are_rejected(int context, int sequences, int? output, double? gpu) =>
        Assert.True(ManagedLimits.OutOfRange(new ManagedLimitsRequest(context, sequences, output, gpu)));

    [Theory]
    [InlineData(true, false, true, true)]    // apply now on a running model: restart
    [InlineData(true, false, false, true)]   // apply now on a stopped model: start with the new limits
    [InlineData(true, true, true, true)]     // another server that is running: restart
    [InlineData(true, true, false, false)]   // another server that is stopped: only save, never start it by surprise
    [InlineData(false, false, true, false)]  // not asked to apply: save path (refused while running)
    [InlineData(false, true, false, false)]
    public void A_stopped_model_on_another_server_is_updated_but_never_started_by_surprise(bool applyNow, bool keepStopped, bool running, bool restarts) =>
        Assert.Equal(restarts, ManagedLimits.WantsRestart(new ManagedLimitsRequest(8192, 1, null, null, applyNow, false, keepStopped), running));

    [Fact]
    public void Ordinary_values_are_accepted() =>
        Assert.False(ManagedLimits.OutOfRange(new ManagedLimitsRequest(262144, 128, 4096, 0.95)));
}
