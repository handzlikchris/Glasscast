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
/// </summary>
public static class SdpCandidates
{
    private const string PublicFoundation = "pub1";

    // Highest host priority, so browsers try the public address first.
    private const uint PublicPriority = 2130706431;

    public static string Rewrite(string sdp, IPAddress? publicIp, int port, bool includeLanCandidates)
    {
        var lines = sdp.Replace("\r\n", "\n").Split('\n').ToList();

        if (!includeLanCandidates)
        {
            lines.RemoveAll(l => l.StartsWith("a=candidate:", StringComparison.Ordinal));
        }

        if (publicIp is not null)
        {
            var candidate = $"a=candidate:{PublicFoundation} 1 udp {PublicPriority} {publicIp} {port} typ host generation 0";
            var midIndex = lines.FindIndex(l => l.StartsWith("a=mid:", StringComparison.Ordinal));
            lines.Insert(midIndex >= 0 ? midIndex + 1 : lines.Count, candidate);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\r\n", lines) + "\r\n";
    }
}
