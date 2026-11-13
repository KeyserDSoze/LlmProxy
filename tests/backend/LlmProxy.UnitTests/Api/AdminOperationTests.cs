using LlmProxy.Api.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmProxy.UnitTests.Api;

public sealed class AdminOperationTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private static AdminOperationService Service(Lifetime? lifetime = null) =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), lifetime ?? new Lifetime(), NullLogger<AdminOperationService>.Instance);

    private static async Task<AdminOperation> Settled(AdminOperationService service, Guid id)
    {
        for (var i = 0; i < 200; i++)
        {
            var operation = service.Get(id)!;
            if (operation.Status != AdminOperationStatus.Running) return operation;
            await Task.Delay(10);
        }
        throw new TimeoutException("Operation did not settle.");
    }

    [Fact]
    public async Task A_started_operation_returns_at_once_and_reports_success()
    {
        var service = Service();
        var release = new TaskCompletionSource();
        var operation = service.Start("model.remove", "Uninstall", null, Guid.NewGuid(),
            async (_, _) => { await release.Task; return new AdminOperationOutcome(true, "Uninstalled."); })!;

        Assert.Equal(AdminOperationStatus.Running, service.Get(operation.Id)!.Status);
        release.SetResult();
        var done = await Settled(service, operation.Id);

        Assert.Equal(AdminOperationStatus.Succeeded, done.Status);
        Assert.Equal("Uninstalled.", done.Detail);
        Assert.NotNull(done.FinishedAtUtc);
    }

    [Fact]
    public async Task A_failed_outcome_keeps_the_agent_message_readable()
    {
        var service = Service();
        var operation = service.Start("model.remove", "Uninstall", null, null,
            (_, _) => Task.FromResult(new AdminOperationOutcome(false, "Management agent returned HTTP 409: busy")))!;

        var done = await Settled(service, operation.Id);

        Assert.Equal(AdminOperationStatus.Failed, done.Status);
        Assert.Contains("HTTP 409: busy", done.Error);
    }

    [Fact]
    public async Task An_exception_becomes_a_failed_operation_instead_of_a_lost_one()
    {
        var service = Service();
        var operation = service.Start("model.stop", "Stop", null, null, (_, _) => throw new InvalidOperationException("boom"))!;

        var done = await Settled(service, operation.Id);

        Assert.Equal(AdminOperationStatus.Failed, done.Status);
        Assert.Contains("boom", done.Error);
    }

    [Fact]
    public async Task A_second_operation_on_the_same_deployment_is_refused_while_one_runs()
    {
        var service = Service();
        var deployment = Guid.NewGuid();
        var release = new TaskCompletionSource();
        var first = service.Start("model.stop", "Stop", null, deployment, async (_, _) => { await release.Task; return new AdminOperationOutcome(true, "ok"); })!;

        Assert.Null(service.Start("model.remove", "Uninstall", null, deployment, (_, _) => Task.FromResult(new AdminOperationOutcome(true, "ok"))));
        release.SetResult();
        await Settled(service, first.Id);
        Assert.NotNull(service.Start("model.remove", "Uninstall", null, deployment, (_, _) => Task.FromResult(new AdminOperationOutcome(true, "ok"))));
    }

    [Theory]
    [InlineData("/api/admin/model-management/deployments/x", 502, 424)]
    [InlineData("/api/admin/nodes", 504, 424)]
    [InlineData("/api/me/keys", 502, 424)]
    [InlineData("/api/admin/nodes", 500, 500)]
    [InlineData("/api/admin/nodes", 404, 404)]
    [InlineData("/v1/chat/completions", 502, 502)]
    [InlineData("/v1/chat/completions", 504, 504)]
    public async Task Proxy_swallowed_statuses_are_remapped_only_for_the_admin_app(string path, int original, int expected)
    {
        var context = new DefaultHttpContext();
        var feature = new StartingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(feature);
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var middleware = new AdminErrorStatusMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = original;
            await ctx.Response.WriteAsync("{}");
        });

        await middleware.InvokeAsync(context);
        await feature.RunStartingCallbacksAsync();

        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Equal(expected != original, context.Response.Headers.ContainsKey(AdminErrorStatusMiddleware.OriginalStatusHeader));
    }

    private sealed class StartingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _starting = [];
        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Add((callback, state));
        public async Task RunStartingCallbacksAsync()
        {
            foreach (var (callback, state) in Enumerable.Reverse(_starting)) await callback(state);
        }
    }
}
