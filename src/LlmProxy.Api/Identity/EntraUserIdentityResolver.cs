using System.Security.Claims;

namespace LlmProxy.Api.Identity;

public sealed record EntraUserIdentity(
    string TenantId,
    string ObjectId,
    string? PrincipalName,
    string? DisplayName,
    IReadOnlyList<string> Roles)
{
    public string AuditActor => $"entra:{TenantId}/{ObjectId}";
}

public static class EntraUserIdentityResolver
{
    private static readonly string[] TenantClaimTypes =
    [
        "tid",
        "http://schemas.microsoft.com/identity/claims/tenantid"
    ];

    private static readonly string[] ObjectClaimTypes =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier"
    ];

    public static bool TryResolve(ClaimsPrincipal principal, out EntraUserIdentity identity)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            identity = null!;
            return false;
        }

        var tenantId = FirstClaim(principal, TenantClaimTypes);
        var objectId = FirstClaim(principal, ObjectClaimTypes);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
        {
            identity = null!;
            return false;
        }

        var principalName = principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.Identity?.Name;
        var displayName = principal.FindFirst("name")?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;
        var roles = principal.Claims
            .Where(claim => claim.Type is ClaimTypes.Role or "roles")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        identity = new EntraUserIdentity(
            tenantId.Trim(),
            objectId.Trim(),
            string.IsNullOrWhiteSpace(principalName) ? null : principalName.Trim(),
            string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            roles);
        return true;
    }

    private static string? FirstClaim(ClaimsPrincipal principal, IEnumerable<string> claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
