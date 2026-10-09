using LlmProxy.NodeAgent;

var builder = WebApplication.CreateBuilder(args);
var options = NodeAgentOptions.From(builder.Configuration);

if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(options.BearerToken))
{
    throw new InvalidOperationException("NodeAgent:BearerToken is required in Production.");
}

Directory.CreateDirectory(options.DataDirectory);
Directory.CreateDirectory(options.ModelCacheDirectory);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddSingleton<HardwareInventoryReader>();
builder.Services.AddSingleton<ManagedModelRegistry>();
builder.Services.AddSingleton<DockerModelRuntimeManager>();
builder.Services.AddHttpClient("gateway", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<OutboundNodeConnectionWorker>();
builder.Services.AddHostedService<OutboundAgentRelayWorker>();
builder.Services.AddHttpClient("relay-local", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("runtime-health", client => client.Timeout = TimeSpan.FromSeconds(5));

var app = builder.Build();
app.UseMiddleware<BearerTokenMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "llmproxy-node-agent" }));
app.MapGet("/v1/system", async (HardwareInventoryReader inventory, CancellationToken cancellationToken) =>
    Results.Ok(await inventory.ReadAsync(cancellationToken)));
app.MapGet("/v1/models", async (DockerModelRuntimeManager manager, CancellationToken cancellationToken) =>
    Results.Ok(await manager.ListAsync(cancellationToken)));
app.MapPost("/v1/models/install", async (InstallRequest request, DockerModelRuntimeManager manager, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await manager.InstallAsync(request, cancellationToken)); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway); }
});
app.MapPost("/v1/models/{installationId}/start", async (string installationId, DockerModelRuntimeManager manager, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await manager.StartAsync(installationId, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or OperationCanceledException)
    { return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway); }
});
app.MapPost("/v1/models/{installationId}/stop", async (string installationId, DockerModelRuntimeManager manager, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await manager.StopAsync(installationId, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
app.MapDelete("/v1/models/{installationId}", async (string installationId, DockerModelRuntimeManager manager, CancellationToken cancellationToken) =>
{
    try { await manager.RemoveAsync(installationId, cancellationToken); return Results.NoContent(); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

app.Run();
