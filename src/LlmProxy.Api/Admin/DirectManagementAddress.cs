using System.Net;
using System.Net.Sockets;

namespace LlmProxy.Api.Admin;

/// <summary>
/// A "direct" Agent registers the address the gateway will later call with a bearer the agent chose. The enrolment
/// token proves the caller was invited, not that the address is sensible, so it must not point at the gateway host
/// itself, a cloud metadata service or a non-unicast address.
/// </summary>
public static class DirectManagementAddress
{
    public static bool IsAcceptable(string? raw, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.Port != 9900 ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 64))
        {
            return false;
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !IsUsable(ip)) return false;
        if (uri.HostNameType == UriHostNameType.Dns &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))) return false;

        address = uri;
        return true;
    }

    private static bool IsUsable(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.Broadcast) || ip.Equals(IPAddress.None)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6Multicast);
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 169 && bytes[1] == 254) return false;   // link-local, includes the cloud metadata address
        if (bytes[0] >= 224) return false;                      // multicast and reserved
        if (bytes[0] == 0) return false;
        return true;
    }
}
