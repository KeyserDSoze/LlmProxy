using System.Text.Json.Serialization;
using LlmProxy.Api.Admin;
using LlmProxy.Api.OpenAi;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Routing;
using LlmProxy.Infrastructure.Health;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);
var entraEnabled = builder.Configuration.GetValue<bool>("EntraId:Enabled");

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddDbContext<GatewayDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddScoped<DatabaseBootstrapper>();
builder.Services.AddScoped<IDeploymentCatalog, EfDeploymentCatalog>();
builder.Services.AddSingleton<IRequestLoadTracker, InMemoryRequestLoadTracker>();
builder.Services.AddSingleton<IRouteSelector, WeightedLeastLoadedRouteSelector>();
builder.Services.AddScoped<RoutingService>();

builder.Services.AddHttpClient("vllm", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("health", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHostedService<NodeHealthMonitor>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminRead", policy => policy.RequireRole("LlmProxy.Admin", "LlmProxy.Reader"));
    options.AddPolicy("AdminWrite", policy => policy.RequireRole("LlmProxy.Admin"));
});

if (entraEnabled)
{
    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraId"));
}

var app = builder.Build();

if (app.Environment.IsProduction() && !entraEnabled)
{
    throw new InvalidOperationException("EntraId:Enabled must be true in Production.");
}

using (var scope = app.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<DatabaseBootstrapper>();
    await bootstrapper.InitializeAsync();
}

app.UseStaticFiles();

if (entraEnabled)
{
    app.UseAuthentication();
}

app.UseAuthorization();
app.UseMiddleware<InferenceApiKeyMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    service = "llmproxy",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/readyz", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapOpenAiEndpoints();
app.MapAdminEndpoints(entraEnabled);

if (entraEnabled)
{
    app.MapGet("/auth/login", () => Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/admin/" },
        [OpenIdConnectDefaults.AuthenticationScheme]));

    app.MapGet("/auth/logout", () => Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
}

app.MapGet("/", () => Results.Redirect("/admin/"));
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
app.Run();
