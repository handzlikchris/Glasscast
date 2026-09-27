using System.Net;

namespace GlassesRemote.Server.Media;

/// <summary>
/// Adjusts the ICE candidates in our SDP offer.
///
/// The router forwards one UDP port straight to this PC, so we tell the browser
/// "send media to &lt;public IP&gt;:&lt;port&gt;" by adding a host candidate for the public
/// address. The browser's connectivity checks arrive through the port forward,
/// and SIPSorcery learns the browser's address as a peer-reflexive candidate.
/// No STUN or TURN server is involved.
///
/// Glasses at home (behind the same router) also get the PC's LAN address, ranked above the
/// public one: they check it first, and when it answers the video goes straight over the LAN
/// instead of out to the router and back (hairpin). If it doesn't answer (the glasses relayed
/// by a phone on mobile data, a guest network), the public address still works.
/// </summary>
public static class SdpCandidates
{
    private const string PublicFoundation = "pub1";
    private const string LanFoundation = "lan1";

    // Highest host priority for the LAN address, then the public one, so browsers try them in
    // that order (and SIPSorcery nominates the first pair that succeeds).
    private const uint LanPriority = 2130706431;
    private const uint PublicPriority = 2130706175;

    /// <param name="lanIp">The PC's LAN address to offer first (glasses at home), or null.</param>
    public static string Rewrite(string sdp, IPAddress? publicIp, int port, bool includeLanCandidates, IPAddress? lanIp = null)
    {
        var lines = sdp.Replace("\r\n", "\n").Split('\n').ToList();

        if (!includeLanCandidates)
        {
            lines.RemoveAll(l => l.StartsWith("a=candidate:", StringComparison.Ordinal));
        }

        var added = new List<string>();
        if (lanIp is not null)
        {
            added.Add($"a=candidate:{LanFoundation} 1 udp {LanPriority} {lanIp} {port} typ host generation 0");
        }
        if (publicIp is not null)
        {
            added.Add($"a=candidate:{PublicFoundation} 1 udp {PublicPriority} {publicIp} {port} typ host generation 0");
        }
        if (added.Count > 0)
        {
            var midIndex = lines.FindIndex(l => l.StartsWith("a=mid:", StringComparison.Ordinal));
            lines.InsertRange(midIndex >= 0 ? midIndex + 1 : lines.Count, added);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\r\n", lines) + "\r\n";
    }
}
