using System.Text.Json.Serialization;
using LlmProxy.Api.Admin;
using LlmProxy.Api.Identity;
using LlmProxy.Api.Observability;
using LlmProxy.Api.OpenAi;
using LlmProxy.Api.Product;
using LlmProxy.Api.Security;
using LlmProxy.Api.SystemOne;
using LlmProxy.Application.Abstractions;
using LlmProxy.Application.Governance;
using LlmProxy.Application.Routing;
using LlmProxy.Domain.Routing;
using LlmProxy.Infrastructure.Governance;
using LlmProxy.Infrastructure.Health;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Retention;
using LlmProxy.Infrastructure.Routing;
using LlmProxy.Infrastructure.Runtime;
using LlmProxy.Infrastructure.Security;
using LlmProxy.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);
var entraEnabled = builder.Configuration.GetValue<bool>("EntraId:Enabled");
var reverseProxyEnabled = builder.Configuration.GetValue<bool>("ReverseProxy:Enabled");
var redisEnabled = builder.Configuration.GetValue<bool>("Redis:Enabled");
var configuredRoutingStrategy = ParseRoutingStrategy(builder.Configuration["Routing:Strategy"]);
var systemOneTimeoutSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("SystemOne:TimeoutSeconds") ?? 30, 1, 300);

builder.AddLlmProxyOpenTelemetry();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ReleaseDiscoveryService>();
builder.Services.AddSingleton<UpdateAgentClient>();

builder.Services.AddSingleton<IApiCredentialCache, InMemoryApiCredentialCache>();
builder.Services.AddSingleton<IRouteCatalog, InMemoryRouteCatalog>();
builder.Services.AddSingleton<IDeploymentCatalog>(services => services.GetRequiredService<IRouteCatalog>());
builder.Services.AddSingleton<InMemoryRateLimitCounterStore>();
builder.Services.AddSingleton<InMemoryOutputTokenBudgetStore>();
builder.Services.AddSingleton<InMemoryRequestLoadTracker>();
builder.Services.AddSingleton<IRequestLoadTracker>(services => services.GetRequiredService<InMemoryRequestLoadTracker>());

if (redisEnabled)
{
    builder.Services.AddSingleton<RedisCoordinationConnection>();
    builder.Services.AddSingleton<RedisNodeMaintenanceCoordinator>();
    builder.Services.AddSingleton<INodeMaintenanceCoordinator>(services => services.GetRequiredService<RedisNodeMaintenanceCoordinator>());
    builder.Services.AddSingleton<IRateLimitCounterStore, RedisRateLimitCounterStore>();
    builder.Services.AddSingleton<IOutputTokenBudgetStore, RedisOutputTokenBudgetStore>();
    builder.Services.AddSingleton<IRequestCapacityGate, RedisRequestCapacityGate>();
    builder.Services.AddSingleton<RedisRuntimeStateCoordinator>();
    builder.Services.AddSingleton<IRuntimeStateEventSink>(services => services.GetRequiredService<RedisRuntimeStateCoordinator>());
    builder.Services.AddSingleton<IRuntimeStateTransportPublisher, RedisRuntimeStateTransportPublisher>();
    builder.Services.AddSingleton<IRuntimeStateDurablePublisher, RuntimeStateOutboxPublisher>();
    builder.Services.AddSingleton<RuntimeStateOutboxSaveChangesInterceptor>();
    builder.Services.AddHostedService(services => services.GetRequiredService<RedisRuntimeStateCoordinator>());
    builder.Services.AddHostedService<RuntimeStateOutboxWorker>();
}
else
{
    builder.Services.AddSingleton<LocalNodeMaintenanceCoordinator>();
    builder.Services.AddSingleton<INodeMaintenanceCoordinator>(services => services.GetRequiredService<LocalNodeMaintenanceCoordinator>());
    builder.Services.AddSingleton<IRateLimitCounterStore>(services => services.GetRequiredService<InMemoryRateLimitCounterStore>());
    builder.Services.AddSingleton<IOutputTokenBudgetStore>(services => services.GetRequiredService<InMemoryOutputTokenBudgetStore>());
    builder.Services.AddSingleton<IRequestCapacityGate, LocalRequestCapacityGate>();
    builder.Services.AddSingleton<IRuntimeStateEventSink, NullRuntimeStateEventSink>();
}

builder.Services.AddSingleton<RequestRateLimiter>();
builder.Services.AddSingleton<OutputTokenBudgetLimiter>();
builder.Services.AddSingleton<ApiCredentialCacheSaveChangesInterceptor>();
builder.Services.AddSingleton<RouteCatalogSaveChangesInterceptor>();
builder.Services.AddSingleton<RateLimitPolicyRuntimeStateInterceptor>();
builder.Services.AddDbContext<GatewayDbContext>((services, options) =>
{
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
        .AddInterceptors(
            services.GetRequiredService<ApiCredentialCacheSaveChangesInterceptor>(),
            services.GetRequiredService<RouteCatalogSaveChangesInterceptor>(),
            services.GetRequiredService<RateLimitPolicyRuntimeStateInterceptor>());

    if (redisEnabled)
    {
        options.AddInterceptors(services.GetRequiredService<RuntimeStateOutboxSaveChangesInterceptor>());
    }
});

builder.Services.AddSingleton<ApiKeyHasher>();
builder.Services.AddSingleton<UpstreamCredentialProtector>();
builder.Services.AddSingleton<SensitiveDataProtector>();
builder.Services.AddScoped<DatabaseBootstrapper>();
builder.Services.AddScoped<DataRetentionService>();
builder.Services.AddHostedService<DataRetentionWorker>();
builder.Services.AddScoped<ContentLogRetentionService>();
builder.Services.AddHostedService<ContentLogRetentionWorker>();
builder.Services.AddSingleton<IDeploymentPerformanceTracker, InMemoryDeploymentPerformanceTracker>();
builder.Services.AddSingleton<INodeRuntimeMetricsTracker, VllmRuntimeMetricsTracker>();
builder.Services.AddSingleton<INodeHardwareMetricsTracker, NodeHardwareMetricsTracker>();
builder.Services.AddSingleton(new RoutingStrategyState(configuredRoutingStrategy));
builder.Services.AddSingleton(new RoutingTuningState(RoutingTuningSettings.Default));
builder.Services.AddSingleton<IRouteSelector, DynamicRouteSelector>();
builder.Services.AddScoped<RoutingService>();
builder.Services.AddScoped<MetricsSummaryReader>();
builder.Services.AddScoped<UsageReportingReader>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<BufferedRequestMetricsSink>();
builder.Services.AddSingleton<IRequestMetricsSink, HttpContextRequestMetricsSink>();
builder.Services.AddHostedService(services => services.GetRequiredService<BufferedRequestMetricsSink>());

builder.Services.AddSingleton<BufferedInferenceContentLogSink>();
builder.Services.AddSingleton<IInferenceContentLogSink>(services => services.GetRequiredService<BufferedInferenceContentLogSink>());
builder.Services.AddHostedService(services => services.GetRequiredService<BufferedInferenceContentLogSink>());

builder.Services.AddSingleton<BufferedCredentialUsageSink>();
builder.Services.AddSingleton<ICredentialUsageSink>(services => services.GetRequiredService<BufferedCredentialUsageSink>());
builder.Services.AddHostedService(services => services.GetRequiredService<BufferedCredentialUsageSink>());

builder.Services.AddHttpClient("vllm", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("health", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("probe", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("maintenance", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("runtime-metrics", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("hardware-metrics", client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("node-management", client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddHttpClient("github-releases", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LlmProxy-ReleaseDiscovery/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
});
builder.Services.AddHttpClient("update-agent", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("system-one", client => client.Timeout = TimeSpan.FromSeconds(systemOneTimeoutSeconds));
builder.Services.AddHostedService<NodeHealthMonitor>();
builder.Services.AddHostedService<VllmRuntimeMetricsCollector>();
builder.Services.AddHostedService<NodeHardwareMetricsCollector>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminRead", policy => policy.RequireRole("LlmProxy.Admin", "LlmProxy.Reader"));
    options.AddPolicy("AdminWrite", policy => policy.RequireRole("LlmProxy.Admin"));
    options.AddPolicy("SelfService", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new PlatformUserAccessRequirement());
    });
});
builder.Services.AddScoped<IAuthorizationHandler, PlatformUserAccessAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationMiddlewareResultHandler>();

if (reverseProxyEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;

        // The production listener is loopback-only when Cloudflare Tunnel is enabled.
        // cloudflared has a dynamic container address, so trust exactly one direct proxy hop.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

if (entraEnabled)
{
    builder.Services.AddTransient<IClaimsTransformation, ConfiguredSuperAdminClaimsTransformation>();
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

if (reverseProxyEnabled)
{
    app.UseForwardedHeaders();
}

app.UseStaticFiles();
app.UseMiddleware<TraceCorrelationMiddleware>();

if (entraEnabled)
{
    app.UseAuthentication();
}

app.UseAuthorization();
app.UseMiddleware<InferenceApiKeyMiddleware>();
app.UseMiddleware<InferenceContentLoggingMiddleware>();
app.UseMiddleware<OutputTokenBudgetMiddleware>();

app.MapGet("/healthz", (RoutingStrategyState strategyState) => Results.Ok(new
{
    status = "ok",
    service = "llmproxy",
    version = ProductReleaseCatalog.GetInfo().Version,
    routingStrategy = strategyState.Current,
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/readyz", async (GatewayDbContext dbContext, CancellationToken cancellationToken) =>
    await dbContext.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapOpenAiEndpoints();
app.MapSystemOneEndpoints();
app.MapIdentitySelfServiceEndpoints(entraEnabled);
app.MapIdentityAdminEndpoints(entraEnabled);
app.MapPlatformUserAdminEndpoints(entraEnabled);
app.MapPlatformUserGroupEndpoints(entraEnabled);
app.MapAdminEndpoints(entraEnabled);
app.MapNodeMaintenanceAdminEndpoints(entraEnabled);
app.MapCredentialRotationAdminEndpoints(entraEnabled);
app.MapCredentialGovernanceAdminEndpoints(entraEnabled);
app.MapApiCredentialSecretAdminEndpoints(entraEnabled);
app.MapContentLogsAdminEndpoints(entraEnabled);
app.MapTestingAdminEndpoints(entraEnabled);
app.MapMetricsAdminEndpoints(entraEnabled);
app.MapRoutingTuningEndpoints(entraEnabled);
app.MapRouteCatalogAdminEndpoints(entraEnabled);
app.MapRuntimeStateAdminEndpoints(entraEnabled);
app.MapNodeHardwareMetricsEndpoints(entraEnabled);
app.MapNodeModelManagementEndpoints(entraEnabled);
app.MapCapacityAdminEndpoints(entraEnabled);
app.MapUsageGovernanceEndpoints(entraEnabled);
app.MapUserRateLimitAdminEndpoints(entraEnabled);
app.MapUserTokenBudgetAdminEndpoints(entraEnabled);
app.MapUsageGroupRateLimitAdminEndpoints(entraEnabled);
app.MapOutputTokenBudgetAdminEndpoints(entraEnabled);
app.MapGovernanceCredentialEndpoints(entraEnabled);
app.MapDataRetentionAdminEndpoints(entraEnabled);
app.MapProductReleaseAdminEndpoints(entraEnabled);
app.MapProductUpdateAdminEndpoints(entraEnabled);

if (entraEnabled)
{
    app.MapGet("/auth/login", () => Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/admin/" },
        [OpenIdConnectDefaults.AuthenticationScheme]));

    app.MapGet("/auth/user-login", () => Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/admin/me" },
        [OpenIdConnectDefaults.AuthenticationScheme]));

    app.MapGet("/auth/logout", () => Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
}

app.MapGet("/", () => Results.Redirect("/admin/"));

if (entraEnabled)
{
    // Make authentication a top-level browser navigation instead of an XHR/OIDC redirect.
    // The latter is blocked by browsers as a cross-origin fetch and surfaces as "Failed to fetch".
    app.MapFallbackToFile("/admin/me", "admin/index.html").RequireAuthorization("SelfService");
    app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html").RequireAuthorization("AdminRead");
}
else
{
    app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
}

app.Run();

static RoutingStrategy ParseRoutingStrategy(string? value)
{
    var raw = string.IsNullOrWhiteSpace(value) ? nameof(RoutingStrategy.WeightedLeastLoaded) : value.Trim();
    return Enum.TryParse<RoutingStrategy>(raw, ignoreCase: true, out var strategy) && Enum.IsDefined(strategy)
        ? strategy
        : throw new InvalidOperationException($"Unsupported Routing:Strategy '{raw}'. Supported values: {string.Join(", ", Enum.GetNames<RoutingStrategy>())}.");
}
