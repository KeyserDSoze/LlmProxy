using System.Text.Json.Serialization;
using LlmProxy.Api.Admin;
using LlmProxy.Api.OpenAi;
using LlmProxy.Api.Security;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Routing;
using LlmProxy.Infrastructure.Health;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Routing;
using LlmProxy.Infrastructure.Security;
using LlmProxy.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);
var entraEnabled = builder.Configuration.GetValue<bool>("EntraId:Enabled");
var configuredRoutingStrategy = ParseRoutingStrategy(builder.Configuration["Routing:Strategy"]);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddDbContext<GatewayDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddSingleton<ApiKeyHasher>();
builder.Services.AddScoped<DatabaseBootstrapper>();
builder.Services.AddScoped<IDeploymentCatalog, EfDeploymentCatalog>();
builder.Services.AddSingleton<IRequestLoadTracker, InMemoryRequestLoadTracker>();
builder.Services.AddSingleton<IDeploymentPerformanceTracker, InMemoryDeploymentPerformanceTracker>();
builder.Services.AddSingleton<INodeRuntimeMetricsTracker, VllmRuntimeMetricsTracker>();
builder.Services.AddSingleton<INodeHardwareMetricsTracker, NodeHardwareMetricsTracker>();
builder.Services.AddSingleton<RequestRateLimiter>();
builder.Services.AddSingleton(new RoutingStrategyState(configuredRoutingStrategy));
builder.Services.AddSingleton(new RoutingTuningState(RoutingTuningSettings.Default));
builder.Services.AddSingleton<IRouteSelector, DynamicRouteSelector>();
builder.Services.AddScoped<RoutingService>();
builder.Services.AddScoped<MetricsSummaryReader>();
builder.Services.AddScoped<UsageReportingReader>();

builder.Services.AddSingleton<BufferedRequestMetricsSink>();
builder.Services.AddSingleton<IRequestMetricsSink>(services => services.GetRequiredService<BufferedRequestMetricsSink>());
builder.Services.AddHostedService(services => services.GetRequiredService<BufferedRequestMetricsSink>());

builder.Services.AddHttpClient("vllm", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("health", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("probe", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("runtime-metrics", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("hardware-metrics", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHostedService<NodeHealthMonitor>();
builder.Services.AddHostedService<VllmRuntimeMetricsCollector>();
builder.Services.AddHostedService<NodeHardwareMetricsCollector>();

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

app.MapGet("/healthz", (RoutingStrategyState strategyState) => Results.Ok(new
{
    status = "ok",
    service = "llmproxy",
    routingStrategy = strategyState.Current,
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/readyz", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapOpenAiEndpoints();
app.MapAdminEndpoints(entraEnabled);
app.MapMetricsAdminEndpoints(entraEnabled);
app.MapRoutingTuningEndpoints(entraEnabled);
app.MapNodeHardwareMetricsEndpoints(entraEnabled);
app.MapCapacityAdminEndpoints(entraEnabled);
app.MapUsageGovernanceEndpoints(entraEnabled);
app.MapGovernanceCredentialEndpoints(entraEnabled);

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

static RoutingStrategy ParseRoutingStrategy(string? value)
{
    var raw = string.IsNullOrWhiteSpace(value) ? nameof(RoutingStrategy.WeightedLeastLoaded) : value.Trim();
    return Enum.TryParse<RoutingStrategy>(raw, ignoreCase: true, out var strategy) && Enum.IsDefined(strategy)
        ? strategy
        : throw new InvalidOperationException(
            $"Unsupported Routing:Strategy '{raw}'. Supported values: {string.Join(", ", Enum.GetNames<RoutingStrategy>())}.");
}
