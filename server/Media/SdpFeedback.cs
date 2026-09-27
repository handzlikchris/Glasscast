using System.Text.RegularExpressions;

namespace GlassesRemote.Server.Media;

/// <summary>
/// Tells the browser it may ask us for keyframes. SIPSorcery's offer only lists
/// <c>transport-cc</c> feedback, and without <c>nack pli</c> or <c>ccm fir</c> Chrome doesn't
/// request a keyframe when packets are lost mid-stream: the picture stays broken until the next
/// scheduled keyframe. With them it sends a PLI, which <see cref="FramePump"/> answers at once.
///
/// Plain <c>nack</c> (retransmission) is deliberately not offered: SIPSorcery doesn't resend
/// packets, so the browser would only wait for retransmissions that never come.
///
/// <c>rtcp-rsize</c> (reduced-size RTCP, RFC 5506) makes Chrome send a PLI in a packet of its own
/// instead of inside the next receiver report. That matters: a few seconds into a stream Chrome
/// adds a bandwidth estimate (REMB) to every report, and SIPSorcery keeps only one feedback item
/// per report, so a PLI next to it was never seen (2 of 124 arrived in a session on the glasses).
/// A PLI in a packet of its own is spotted from its unencrypted header
/// (<see cref="SipsorceryMediaPeer.IsStandaloneKeyframeRequest"/>).
/// </summary>
public static partial class SdpFeedback
{
    [GeneratedRegex(@"^a=rtpmap:(\d+) (H264|VP8)/90000$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoRtpmap();

    public static string AddKeyframeRequests(string sdp)
    {
        var lines = sdp.Replace("\r\n", "\n").Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var match = VideoRtpmap().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var pt = match.Groups[1].Value;
            string[] wanted = [$"a=rtcp-fb:{pt} nack pli", $"a=rtcp-fb:{pt} ccm fir", "a=rtcp-rsize"];
            var missing = wanted.Where(w => !lines.Contains(w)).ToArray();
            lines.InsertRange(i + 1, missing);
            i += missing.Length;
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\r\n", lines) + "\r\n";
    }
}
