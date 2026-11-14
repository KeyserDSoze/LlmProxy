using System.Text.Json;
using LlmProxy.Domain.Audit;
using LlmProxy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Removes a published name that nothing serves. The database cascades a model's deployments when the model is deleted,
/// so a name that still has any deployment is refused: its installation has to be removed first, on purpose.
/// </summary>
internal static class ModelUnpublish
{
    public enum Outcome { Removed, NotFound, HasDeployments }

    public static async Task<Outcome> RunAsync(GatewayDbContext db, Guid modelId, string actor, string? sourceIp, CancellationToken token)
    {
        var model = await db.Models.SingleOrDefaultAsync(item => item.Id == modelId, token);
        if (model is null) return Outcome.NotFound;
        if (await db.Deployments.AnyAsync(deployment => deployment.ModelId == modelId, token)) return Outcome.HasDeployments;
        db.Models.Remove(model);
        db.AuditEvents.Add(new AuditEvent(actor, "model.unpublish", "model", modelId.ToString(), sourceIp,
            JsonSerializer.Serialize(new { model.PublicName, model.ProviderModelName })));
        await db.SaveChangesAsync(token);
        return Outcome.Removed;
    }
}
