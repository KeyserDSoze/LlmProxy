using LlmProxy.Api.Admin;
using LlmProxy.Domain.Deployments;
using LlmProxy.Domain.Models;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LlmProxy.UnitTests.Api;

public sealed class ModelUnpublishTests
{
    private static (ServiceProvider Services, ModelDefinition Model) Arrange(bool withDeployment)
    {
        var services = new ServiceCollection()
            .AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")))
            .BuildServiceProvider();
        var db = services.GetRequiredService<GatewayDbContext>();
        var model = new ModelDefinition("meta-muse-glimmer", "meta/muse-glimmer", true, false, ModelSurface.OpenAi);
        db.Models.Add(model);
        if (withDeployment) db.Deployments.Add(new ModelDeployment(Guid.NewGuid(), model.Id));
        db.SaveChanges();
        return (services, model);
    }

    [Fact]
    public async Task A_name_nothing_serves_is_removed_and_the_removal_is_audited()
    {
        var (services, model) = Arrange(withDeployment: false);
        var db = services.GetRequiredService<GatewayDbContext>();

        var outcome = await ModelUnpublish.RunAsync(db, model.Id, "admin@agic.it", "10.0.0.5", CancellationToken.None);

        Assert.Equal(ModelUnpublish.Outcome.Removed, outcome);
        Assert.Empty(db.Models);
        var audit = Assert.Single(db.AuditEvents);
        Assert.Equal("model.unpublish", audit.Action);
        Assert.Equal("admin@agic.it", audit.Actor);
        Assert.Contains("meta/muse-glimmer", audit.DetailsJson);
    }

    [Fact]
    public async Task A_name_with_a_deployment_is_kept_even_when_that_deployment_is_switched_off()
    {
        var (services, model) = Arrange(withDeployment: true);
        var db = services.GetRequiredService<GatewayDbContext>();

        var outcome = await ModelUnpublish.RunAsync(db, model.Id, "admin", null, CancellationToken.None);

        Assert.Equal(ModelUnpublish.Outcome.HasDeployments, outcome);
        Assert.Single(db.Models);
        Assert.Single(db.Deployments);
        Assert.Empty(db.AuditEvents);
    }

    [Fact]
    public async Task An_unknown_name_is_reported_as_not_found()
    {
        var (services, _) = Arrange(withDeployment: false);

        Assert.Equal(ModelUnpublish.Outcome.NotFound,
            await ModelUnpublish.RunAsync(services.GetRequiredService<GatewayDbContext>(), Guid.NewGuid(), "admin", null, CancellationToken.None));
    }
}
