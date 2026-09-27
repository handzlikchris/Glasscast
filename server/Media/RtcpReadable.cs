using System.Buffers.Binary;

namespace GlassesRemote.Server.Media;

/// <summary>
/// Did SIPSorcery manage to decrypt an SRTCP packet from the glasses? Asked on the channel's raw
/// buffer after SIPSorcery has had its go: it decrypts that buffer in place (it subscribed before
/// us), and leaves it encrypted when unprotect fails (HMAC, replay). SRTCP leaves the first 8
/// bytes (header, sender SSRC) in the clear and encrypts the rest, so a field after them that must
/// name our video stream tells the two apart (a random match is a 1 in 2^32 chance).
/// </summary>
public static class RtcpReadable
{
    private const byte ReceiverReport = 201;
    private const byte TransportFeedback = 205; // NACK (FMT 1), transport-cc (FMT 15)
    private const byte PayloadFeedback = 206;
    private const int Pli = 1;

    /// <summary>RTCP rather than RTP, by the packet type (RFC 5761 demultiplexing).</summary>
    public static bool IsRtcp(ReadOnlySpan<byte> packet) =>
        packet.Length >= 8 && packet[0] >> 6 == 2 && packet[1] is >= 192 and <= 223;

    /// <summary>
    /// True when decrypted, false when still encrypted, null when the packet's first item has no
    /// field that must name <paramref name="mediaSsrc"/> (e.g. REMB, FIR, an empty report).
    /// </summary>
    public static bool? Decrypted(ReadOnlySpan<byte> packet, uint mediaSsrc)
    {
        if (!IsRtcp(packet) || packet.Length < 12)
        {
            return null;
        }

        var count = packet[0] & 0x1F;
        var namesOurStream = packet[1] switch
        {
            ReceiverReport => count > 0,         // first report block: the SSRC it reports on
            TransportFeedback => true,           // media source SSRC
            PayloadFeedback => count == Pli,     // PLI names the media source; REMB and FIR put 0 there
            _ => false,
        };
        return namesOurStream ? BinaryPrimitives.ReadUInt32BigEndian(packet[8..]) == mediaSsrc : null;
    }
}
