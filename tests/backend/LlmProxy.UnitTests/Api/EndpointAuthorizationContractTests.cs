using LlmProxy.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LlmProxy.UnitTests.Api;

/// <summary>
/// Route-level authorization contract. Every Admin or self-service route must carry an explicit policy when Entra is
/// enabled, so a new endpoint cannot ship open by forgetting <c>RequireAuthorization</c>.
/// </summary>
public sealed class EndpointAuthorizationContractTests
{
    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS"];

    // Routes that are deliberately not behind an Entra policy. Agents authenticate with an enrolment token or their own
    // credential; the downloads serve the public, checksummed Agent release files.
    private static readonly HashSet<string> OutsideEntra = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/agent-connection/{nodeId:guid}/tunnel",
        "/api/agent-connection/{nodeId:guid}/heartbeat",
        "/api/agent-connection/enroll",
        "/api/agent-connection/recover",
        "/downloads/agent/{version}/{fileName}",
        "/downloads/agent/connect-node.sh",
        "/downloads/agent/connect-node.sh.sha256",
        "/downloads/agent/version"
    };

    /// <summary>
    /// Minimal API parameter inference asks the container which parameter types are services. The production container is
    /// not built here, so every interface and every non-record class of the product assemblies is declared as a service
    /// (never resolved); records stay request bodies.
    /// </summary>
    private static void DeclareProductServices(IServiceCollection services)
    {
        var assemblies = new[] { typeof(GatewayEndpointMap).Assembly, typeof(LlmProxy.Application.Abstractions.IDeploymentCatalog).Assembly,
            typeof(LlmProxy.Infrastructure.Persistence.GatewayDbContext).Assembly, typeof(LlmProxy.Domain.Nodes.InferenceNode).Assembly };
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            if (type.IsGenericTypeDefinition || type.IsNested || type.IsAbstract && type.IsSealed) continue;
            var isRecord = type.GetMethod("<Clone>$") is not null;
            if (type.IsInterface || (type.IsClass && !isRecord))
                services.AddSingleton(type, _ => throw new InvalidOperationException("Not resolvable in the route inventory test."));
        }
    }

    private sealed record Route(string Pattern, string[] Methods, string[] Policies, bool Anonymous)
    {
        public bool IsWrite => Methods.Length == 0 || Methods.Any(method => !SafeMethods.Contains(method, StringComparer.OrdinalIgnoreCase));
        public override string ToString() => $"{string.Join('|', Methods)} {Pattern} [{string.Join(',', Policies)}]";
    }

    private static List<Route> Routes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        DeclareProductServices(builder.Services);
        builder.Services.AddHttpClient();
        using var app = builder.Build();
        ((IEndpointRouteBuilder)app).MapGatewayEndpoints(entraEnabled: true);
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => new Route(
                endpoint.RoutePattern.RawText ?? "",
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.ToArray() ?? [],
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy ?? "(default)").ToArray(),
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null))
            .ToList();
    }

    [Fact]
    public void Routes_are_discovered()
    {
        var routes = Routes();
        Assert.Contains(routes, route => route.Pattern.StartsWith("/api/admin/", StringComparison.Ordinal));
        Assert.Contains(routes, route => route.Pattern.StartsWith("/api/me/", StringComparison.Ordinal));
        Assert.Contains(routes, route => route.Pattern.StartsWith("/v1/", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_admin_route_requires_an_admin_policy_and_every_change_requires_write()
    {
        var offenders = Routes()
            .Where(route => route.Pattern.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
            .Where(route => route.Anonymous
                || !route.Policies.Any(policy => policy is "AdminRead" or "AdminWrite")
                || (route.IsWrite && !route.Policies.Contains("AdminWrite")))
            .Select(route => route.ToString())
            .Order()
            .ToList();

        Assert.True(offenders.Count == 0, "Admin routes without the required policy:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_self_service_route_requires_the_self_service_policy()
    {
        var offenders = Routes()
            .Where(route => route.Pattern.StartsWith("/api/me", StringComparison.OrdinalIgnoreCase))
            .Where(route => route.Anonymous || !route.Policies.Contains("SelfService"))
            .Select(route => route.ToString())
            .Order()
            .ToList();

        Assert.True(offenders.Count == 0, "Self-service routes without the SelfService policy:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Self_service_routes_never_accept_the_reader_or_write_policies_as_a_substitute()
    {
        var mixed = Routes()
            .Where(route => route.Pattern.StartsWith("/api/me", StringComparison.OrdinalIgnoreCase))
            .Where(route => route.Policies.Any(policy => policy is "AdminRead" or "AdminWrite"))
            .Select(route => route.ToString())
            .ToList();

        Assert.Empty(mixed);
    }

    [Fact]
    public void Routes_outside_the_known_surfaces_are_reviewed_explicitly()
    {
        var unexpected = Routes()
            .Where(route => !route.Pattern.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)
                && !route.Pattern.StartsWith("/api/me", StringComparison.OrdinalIgnoreCase)
                && !route.Pattern.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
                && !OutsideEntra.Contains(route.Pattern))
            .Select(route => route.ToString())
            .Order()
            .ToList();

        Assert.True(unexpected.Count == 0,
            "New route outside /api/admin, /api/me and /v1. Give it a policy or add it to the reviewed allow-list:\n" + string.Join("\n", unexpected));
    }
}
