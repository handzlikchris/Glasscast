using System.Net;

namespace GlassesRemote.Server.Media;

/// <summary>Which way the video can go: over the LAN when the glasses are at home, else the internet.</summary>
public static class MediaPaths
{
    /// <summary>
    /// The glasses reach the PC from the router's own public address: they're behind the same
    /// router (home network), so the PC's LAN address is reachable too.
    /// </summary>
    public static bool IsHome(IPAddress remote, IPAddress? publicIp) =>
        publicIp is not null && Normalise(remote).Equals(Normalise(publicIp));

    /// <summary>A private (RFC 1918) IPv4 address: the video pair went over the LAN.</summary>
    public static bool IsLan(IPAddress address)
    {
        var bytes = Normalise(address).GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168));
    }

    private static IPAddress Normalise(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
