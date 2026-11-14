namespace LlmProxy.Infrastructure.Persistence;

/// <summary>
/// One ASP.NET Core Data Protection key. The key ring signs and encrypts the Admin sign-in cookie, so it must outlive the
/// container: with keys kept only inside the container every update would invalidate every session. The XML is stored
/// encrypted (see <c>SensitiveDataProtector</c>); anyone with only database access cannot forge a session.
/// </summary>
public sealed class DataProtectionKeyRecord
{
    public string FriendlyName { get; set; } = string.Empty;
    public string XmlCiphertext { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
