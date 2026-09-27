using SIPSorcery.Net;

namespace GlassesRemote.Server.Media;

/// <summary>One RTP packet of a video frame, before SRTP: payload, frame timestamp, marker bit.</summary>
public readonly record struct RtpPacket(byte[] Payload, uint Timestamp, bool Marker);

/// <summary>
/// Splits an H.264 access unit (Annex B) into RTP payloads (RFC 6184, packetization-mode 1)
/// exactly as SIPSorcery's <c>SendVideo</c> does: a NAL that fits goes as is, a larger one as
/// FU-A fragments; the marker bit is on the frame's last packet. Doing it here, rather than in
/// <c>SendVideo</c>, lets us choose when each packet goes out (<see cref="RtpPacer"/>).
/// </summary>
public static class H264Rtp
{
    /// <summary>SIPSorcery's RTP_MAX_PAYLOAD: packets stay under a typical path MTU after SRTP.</summary>
    public const int MaxPayload = 1200;

    public static List<RtpPacket> Packetize(byte[] accessUnit, uint timestamp, int maxPayload = MaxPayload)
    {
        var packets = new List<RtpPacket>();
        foreach (var nal in H264Packetiser.ParseNals(accessUnit))
        {
            var bytes = nal.NAL;
            if (bytes.Length == 0)
            {
                continue;
            }

            if (bytes.Length <= maxPayload)
            {
                packets.Add(new RtpPacket(bytes, timestamp, nal.IsLast));
                continue;
            }

            // FU-A: the NAL header byte is replaced by the FU indicator and header on every fragment.
            var body = bytes.AsSpan(1);
            for (var offset = 0; offset < body.Length; offset += maxPayload)
            {
                var length = Math.Min(maxPayload, body.Length - offset);
                var last = offset + length >= body.Length;
                var header = H264Packetiser.GetH264RtpHeader(bytes[0], offset == 0, last);
                var payload = new byte[header.Length + length];
                header.CopyTo(payload, 0);
                body.Slice(offset, length).CopyTo(payload.AsSpan(header.Length));
                packets.Add(new RtpPacket(payload, timestamp, nal.IsLast && last));
            }
        }

        return packets;
    }
}
