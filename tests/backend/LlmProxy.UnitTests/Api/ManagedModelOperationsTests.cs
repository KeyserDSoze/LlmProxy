using System.Net;
using LlmProxy.Api.Admin;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Nodes;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmProxy.UnitTests.Api;

public sealed class ManagedModelOperationsTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (ServiceProvider Services, Guid DeploymentId, Handler Handler) Arrange(HttpStatusCode agentStatus, string agentBody = "{}")
    {
        var handler = new Handler(agentStatus, agentBody);
        var services = new ServiceCollection()
            .AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")))
            .AddSingleton<IHttpClientFactory>(new Factory(handler))
            .AddSingleton(new UpstreamCredentialProtector(new ConfigurationBuilder().Build()))
            .BuildServiceProvider();
        var db = services.GetRequiredService<GatewayDbContext>();
        var node = new InferenceNode("spark", "http://agent.test:8000");
        node.SetManagementBaseAddress("http://agent.test:9900");
        var deployment = new ModelDeployment(node.Id, Guid.NewGuid());
        deployment.ConfigureRuntime("http://agent.test:18000", "custom:llama.cpp", "custom-01ea8a84");
        deployment.Enable();
        db.Nodes.Add(node);
        db.Deployments.Add(deployment);
        db.SaveChanges();
        return (services, deployment.Id, handler);
    }

    private static readonly ManagedModelOperations.Caller Caller = new("tester", "127.0.0.1");

    [Fact]
    public async Task Uninstall_removes_the_deployment_after_the_agent_confirms()
    {
        var (services, id, handler) = Arrange(HttpStatusCode.NoContent, "");

        var outcome = await ManagedModelOperations.RunAsync(services, ManagedModelOperations.Kind.Remove, id, Caller, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["DELETE /v1/models/custom-01ea8a84"], handler.Calls);
        var db = services.GetRequiredService<GatewayDbContext>();
        Assert.Empty(db.Deployments);
        Assert.Contains(db.AuditEvents, audit => audit.Action == "model.remove" && audit.Actor == "tester");
    }

    [Fact]
    public async Task Uninstall_treats_an_agent_that_no_longer_knows_the_model_as_already_removed()
    {
        var (services, id, _) = Arrange(HttpStatusCode.NotFound, "{\"title\":\"Not Found\"}");

        var outcome = await ManagedModelOperations.RunAsync(services, ManagedModelOperations.Kind.Remove, id, Caller, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Contains("no longer had this installation", outcome.Message);
        Assert.Empty(services.GetRequiredService<GatewayDbContext>().Deployments);
    }

    [Fact]
    public async Task A_failed_uninstall_keeps_the_deployment_disabled_and_records_the_agents_reason()
    {
        var (services, id, _) = Arrange(HttpStatusCode.Conflict, "{\"error\":\"Another install, start, stop or edit operation is already running\"}");

        var outcome = await ManagedModelOperations.RunAsync(services, ManagedModelOperations.Kind.Remove, id, Caller, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("HTTP 409", outcome.Message);
        var db = services.GetRequiredService<GatewayDbContext>();
        var deployment = Assert.Single(db.Deployments);
        Assert.False(deployment.Enabled);
        var failure = Assert.Single(db.AuditEvents, audit => audit.Action == "model.remove.failed");
        Assert.Contains("already running", failure.DetailsJson);
    }

    [Fact]
    public async Task Stop_switches_routing_off_before_calling_the_agent()
    {
        var (services, id, handler) = Arrange(HttpStatusCode.OK, "{\"installationId\":\"custom-01ea8a84\",\"status\":\"stopped\"}");

        var outcome = await ManagedModelOperations.RunAsync(services, ManagedModelOperations.Kind.Stop, id, Caller, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["POST /v1/models/custom-01ea8a84/stop"], handler.Calls);
        Assert.False(Assert.Single(services.GetRequiredService<GatewayDbContext>().Deployments).Enabled);
    }

    [Fact]
    public async Task A_deployment_without_an_agent_is_reported_not_thrown()
    {
        var (services, _, _) = Arrange(HttpStatusCode.OK);

        var outcome = await ManagedModelOperations.RunAsync(services, ManagedModelOperations.Kind.Remove, Guid.NewGuid(), Caller, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("no longer exists", outcome.Message);
    }
}
