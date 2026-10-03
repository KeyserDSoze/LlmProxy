using LlmProxy.UpdateAgent;

var builder = WebApplication.CreateBuilder(args);
var options = UpdateAgentOptions.From(builder.Configuration);

if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(options.BearerToken))
{
    throw new InvalidOperationException("UpdateAgent:BearerToken is required in Production.");
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<UpdateScheduler>();
builder.Services.AddHostedService(services => services.GetRequiredService<UpdateScheduler>());

var app = builder.Build();
app.UseMiddleware<BearerTokenMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "llmproxy-update-agent" }));
app.MapGet("/v1/status", async (UpdateScheduler scheduler, CancellationToken cancellationToken) =>
    Results.Ok(await scheduler.GetSnapshotAsync(cancellationToken)));

app.MapPost("/v1/updates", async (
    ScheduleUpdateRequest request,
    UpdateScheduler scheduler,
    CancellationToken cancellationToken) =>
{
    try
    {
        var job = await scheduler.ScheduleAsync(request, cancellationToken);
        return Results.Accepted($"/v1/updates/{job.Id}", job);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = "invalid_update_request", message = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = "update_conflict", message = exception.Message });
    }
});

app.MapDelete("/v1/updates/{id:guid}", async (
    Guid id,
    UpdateScheduler scheduler,
    CancellationToken cancellationToken) =>
    await scheduler.CancelAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

app.Run();
