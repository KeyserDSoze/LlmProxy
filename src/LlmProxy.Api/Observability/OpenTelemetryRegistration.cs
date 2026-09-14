using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LlmProxy.Api.Observability;

public static class OpenTelemetryRegistration
{
    public static void AddLlmProxyOpenTelemetry(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue("OpenTelemetry:Enabled", false))
        {
            return;
        }

        var endpointRaw = builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317";
        if (!Uri.TryCreate(endpointRaw, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"OpenTelemetry:OtlpEndpoint '{endpointRaw}' is not a valid absolute URI.");
        }

        var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "llmproxy";
        var serviceNamespace = builder.Configuration["OpenTelemetry:ServiceNamespace"] ?? "agic.ai";
        var serviceInstanceId = builder.Configuration["OpenTelemetry:ServiceInstanceId"]
            ?? Environment.GetEnvironmentVariable("HOSTNAME")
            ?? Environment.MachineName;
        var environmentName = builder.Configuration["OpenTelemetry:Environment"]
            ?? builder.Environment.EnvironmentName;

        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceNamespace: serviceNamespace, serviceInstanceId: serviceInstanceId)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment.name"] = environmentName
            });

        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.SetResourceBuilder(resource);
            options.AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint));
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder
                .AddService(serviceName, serviceNamespace: serviceNamespace, serviceInstanceId: serviceInstanceId)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = environmentName
                }))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint)))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint)));
    }

    private static void ConfigureExporter(OtlpExporterOptions exporter, Uri endpoint)
    {
        exporter.Endpoint = endpoint;
        exporter.Protocol = OtlpExportProtocol.Grpc;
    }
}
