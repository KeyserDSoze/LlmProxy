using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace LlmProxy.Api.Identity;

/// <summary>
/// Grants the internal full-administrator role to explicitly configured Entra identities
/// after Microsoft Entra authentication has succeeded. The configured Entra tenant remains
/// the authentication trust boundary; this transformation never authenticates a caller.
/// </summary>
public sealed class ConfiguredSuperAdminClaimsTransformation(IConfiguration configuration) : IClaimsTransformation
{
    public const string AdminRole = "LlmProxy.Admin";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.IsInRole(AdminRole))
        {
            return Task.FromResult(principal);
        }

        var configured = ParseEntries(configuration["EntraId:SuperAdmins"]);
        if (configured.Length == 0)
        {
            return Task.FromResult(principal);
        }

        var objectId = FirstClaim(principal,
            "oid",
            "http://schemas.microsoft.com/identity/claims/objectidentifier");

        var principalNames = principal.Claims
            .Where(claim => claim.Type is
                "preferred_username" or
                "email" or
                "upn" ||
                claim.Type == ClaimTypes.Email ||
                claim.Type == ClaimTypes.Upn)
            .Select(claim => claim.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matched = configured.Any(entry =>
            entry.StartsWith("oid:", StringComparison.OrdinalIgnoreCase)
                ? !string.IsNullOrWhiteSpace(objectId) &&
                  string.Equals(entry[4..].Trim(), objectId, StringComparison.OrdinalIgnoreCase)
                : principalNames.Contains(entry));

        if (!matched)
        {
            return Task.FromResult(principal);
        }

        var identity = principal.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated);
        if (identity is not null &&
            !identity.Claims.Any(claim =>
                claim.Type == identity.RoleClaimType &&
                string.Equals(claim.Value, AdminRole, StringComparison.OrdinalIgnoreCase)))
        {
            identity.AddClaim(new Claim(identity.RoleClaimType, AdminRole));
        }

        return Task.FromResult(principal);
    }

    internal static string[] ParseEntries(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string? FirstClaim(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
