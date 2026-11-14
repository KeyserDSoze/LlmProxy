using System.Xml.Linq;
using LlmProxy.Infrastructure.Persistence;
using LlmProxy.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LlmProxy.Api.Security;

/// <summary>
/// Keeps the Data Protection key ring in PostgreSQL so the Admin sign-in survives a restart or an update of the gateway,
/// and every replica uses the same keys. Each key is encrypted with the gateway's own secret before it is stored.
/// A key that cannot be read (for example after the secret changed) is skipped with a warning: a new key is created and
/// affected users sign in again, nothing else breaks.
/// </summary>
public sealed class DatabaseDataProtectionRepository(
    IServiceScopeFactory scopes, SensitiveDataProtector protector, ILogger<DatabaseDataProtectionRepository> logger) : IXmlRepository
{
    private static string Purpose(string friendlyName) => "data-protection-key:" + friendlyName;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var elements = new List<XElement>();
        foreach (var row in db.DataProtectionKeys.AsNoTracking().OrderBy(key => key.CreatedAtUtc).ToList())
        {
            try { elements.Add(XElement.Parse(protector.Unprotect(row.XmlCiphertext, Purpose(row.FriendlyName)))); }
            catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or System.Xml.XmlException)
            {
                logger.LogWarning("Data Protection key {Key} could not be read ({Error}); it is ignored and sessions that used it must sign in again.",
                    row.FriendlyName, error.GetType().Name);
            }
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var ciphertext = protector.Protect(element.ToString(SaveOptions.DisableFormatting), Purpose(friendlyName));
        var existing = db.DataProtectionKeys.SingleOrDefault(key => key.FriendlyName == friendlyName);
        if (existing is null)
            db.DataProtectionKeys.Add(new DataProtectionKeyRecord { FriendlyName = friendlyName, XmlCiphertext = ciphertext });
        else
            existing.XmlCiphertext = ciphertext;
        db.SaveChanges();
    }
}
