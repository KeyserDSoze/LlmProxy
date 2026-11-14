using System.Net;
using System.Text;
using LlmProxy.Api.Admin;
using LlmProxy.Application.Abstractions;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmProxy.UnitTests.Api;

public sealed class RuntimeEngineRestarterTests
{
    /// <summary>An Agent that runs two llama.cpp models and one vLLM model; the start of the models named in <c>failing</c> fails.</summary>
    private sealed class FakeAgent(Func<string, bool> routingIsOff, ISet<string> failing) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        private readonly Dictionary<Guid, (string Installation, bool Fails)> _jobs = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method + " " + path);
            string Json(string body) => body;
            if (path == "/v1/models")
                return Reply(Json("{\"models\":[" +
                    "{\"installationId\":\"a\",\"providerModelName\":\"org/a\",\"status\":\"running\",\"runtime\":\"llama.cpp\",\"port\":18000}," +
                    "{\"installationId\":\"b\",\"providerModelName\":\"org/b\",\"status\":\"running\",\"runtime\":\"llama.cpp\",\"port\":18001}," +
                    "{\"installationId\":\"c\",\"providerModelName\":\"org/c\",\"status\":\"running\",\"runtime\":\"vllm\",\"port\":18002}," +
                    "{\"installationId\":\"d\",\"providerModelName\":\"org/d\",\"status\":\"stopped\",\"runtime\":\"llama.cpp\",\"port\":18003}]}"));
            if (path.EndsWith("/stop"))
            {
                Assert.True(routingIsOff(path.Split('/')[3]), "routing must be off for a model before it is stopped");
                return Reply("{}");
            }
            if (path.EndsWith("/start-jobs") && request.Method == HttpMethod.Post)
            {
                var installation = path.Split('/')[3];
                var id = Guid.NewGuid();
                _jobs[id] = (installation, failing.Contains(installation));
                return Reply("{\"id\":\"" + id + "\",\"installationId\":\"" + installation + "\",\"status\":\"queued\",\"detail\":\"\",\"createdAtUtc\":\"2026-11-14T10:00:00Z\"}");
            }
            if (path == "/v1/models/start-jobs")
                return Reply("[" + string.Join(",", _jobs.Select(job =>
                    "{\"id\":\"" + job.Key + "\",\"installationId\":\"" + job.Value.Installation + "\",\"status\":\"" + (job.Value.Fails ? "failed" : "completed") +
                    "\",\"detail\":\"done\",\"error\":" + (job.Value.Fails ? "\"out of memory\\nmore\"" : "null") + ",\"createdAtUtc\":\"2026-11-14T10:00:00Z\"}")) + "]");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Reply(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Load(Func<Guid, int> active) : IRequestLoadTracker
    {
        public int GetActive(Guid deploymentId) => active(deploymentId);
        public IDisposable Enter(Guid deploymentId) => throw new NotSupportedException();
        public bool TryEnter(Guid deploymentId, Guid nodeId, int deploymentMaxConcurrency, int nodeMaxConcurrency, out IDisposable? lease) => throw new NotSupportedException();
    }

    private static (ServiceProvider Services, Guid NodeId, FakeAgent Agent) Arrange(ISet<string> failing, Func<Guid, int>? active = null)
    {
        RuntimeEngineRestarter.Poll = TimeSpan.FromMilliseconds(10);
        ServiceProvider? services = null;
        var agent = new FakeAgent(installation => !services!.GetRequiredService<GatewayDbContext>().Deployments.AsNoTracking()
            .Single(deployment => deployment.ManagedInstallationId == installation).Enabled, failing);
        services = new ServiceCollection()
            .AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")))
            .AddSingleton<IHttpClientFactory>(new Factory(agent))
            .AddSingleton(new UpstreamCredentialProtector(new ConfigurationBuilder().Build()))
            .AddSingleton<IRequestLoadTracker>(new Load(active ?? (_ => 0)))
            .BuildServiceProvider();
        var db = services.GetRequiredService<GatewayDbContext>();
        var node = new InferenceNode("spark", "http://agent.test:8000");
        node.SetManagementBaseAddress("http://agent.test:9900");
        db.Nodes.Add(node);
        foreach (var installation in new[] { "a", "b", "c" })
        {
            var deployment = new ModelDeployment(node.Id, Guid.NewGuid());
            deployment.ConfigureRuntime("http://agent.test:18000", "custom", installation);
            deployment.Enable();
            db.Deployments.Add(deployment);
        }
        db.SaveChanges();
        return (services, node.Id, agent);
    }

    private static bool Routed(ServiceProvider services, string installation) =>
        services.GetRequiredService<GatewayDbContext>().Deployments.AsNoTracking().Single(deployment => deployment.ManagedInstallationId == installation).Enabled;

    [Fact]
    public async Task Only_the_running_models_of_that_engine_are_restarted_one_at_a_time_and_put_back_in_routing()
    {
        var (services, nodeId, agent) = Arrange(new HashSet<string>());
        var progress = new List<string>();

        var outcome = await RuntimeEngineRestarter.RestartRunningModelsAsync(services, nodeId, "llama.cpp", "llama.cpp", progress.Add, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("2 models restarted on the new llama.cpp.", outcome.Message);
        Assert.Equal(["POST /v1/models/a/stop", "POST /v1/models/a/start-jobs", "POST /v1/models/b/stop", "POST /v1/models/b/start-jobs"],
            agent.Calls.Where(call => call.Contains("/stop") || (call.StartsWith("POST") && call.EndsWith("/start-jobs"))));
        Assert.All(new[] { "a", "b", "c" }, installation => Assert.True(Routed(services, installation)));
        Assert.Contains(progress, line => line.Contains("org/a") && line.Contains("1 of 2"));
        Assert.DoesNotContain(agent.Calls, call => call.Contains("/c/") || call.Contains("/d/"));
    }

    [Fact]
    public async Task A_model_that_does_not_come_back_keeps_routing_off_and_stops_the_sequence()
    {
        var (services, nodeId, agent) = Arrange(new HashSet<string> { "a" });

        var outcome = await RuntimeEngineRestarter.RestartRunningModelsAsync(services, nodeId, "llama.cpp", "llama.cpp", _ => { }, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("org/a did not come back", outcome.Message);
        Assert.Contains("out of memory", outcome.Message);
        Assert.Contains("Routing for it stays off", outcome.Message);
        Assert.False(Routed(services, "a"));
        Assert.True(Routed(services, "b"));
        Assert.DoesNotContain(agent.Calls, call => call == "POST /v1/models/b/stop");
    }

    [Fact]
    public async Task Requests_in_flight_are_given_time_to_finish_before_the_model_is_stopped()
    {
        var remaining = 3;
        var (services, nodeId, agent) = Arrange(new HashSet<string>(), _ => Math.Max(0, remaining--));

        var outcome = await RuntimeEngineRestarter.RestartRunningModelsAsync(services, nodeId, "llama.cpp", "llama.cpp", _ => { }, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(remaining <= 0, "the restarter polled the load until it reached zero");
    }

    [Fact]
    public async Task Nothing_is_touched_when_no_running_model_uses_the_engine()
    {
        var (services, nodeId, agent) = Arrange(new HashSet<string>());

        var outcome = await RuntimeEngineRestarter.RestartRunningModelsAsync(services, nodeId, "sglang", "SGLang", _ => { }, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Contains("No running model uses SGLang", outcome.Message);
        Assert.Equal(["GET /v1/models"], agent.Calls);
        Assert.All(new[] { "a", "b", "c" }, installation => Assert.True(Routed(services, installation)));
    }
}
