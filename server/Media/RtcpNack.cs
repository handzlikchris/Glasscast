using System.Buffers.Binary;

namespace GlassesRemote.Server.Media;

/// <summary>
/// Reads generic NACKs (RFC 4585 §6.2.1: RTPFB, FMT 1) out of a decrypted RTCP packet: the RTP
/// sequence numbers the glasses are missing. SIPSorcery's own parser keeps only the first entry
/// of the first NACK in a packet, and Chrome lists up to dozens of packets in one.
/// </summary>
public static class RtcpNack
{
    private const byte Rtpfb = 205;
    private const int GenericNack = 1;

    /// <summary>A NACK first in the packet (reduced-size RTCP), judged by the unencrypted header.</summary>
    public static bool IsStandalone(ReadOnlySpan<byte> packet) =>
        packet.Length >= 16
        && packet[0] >> 6 == 2
        && packet[1] == Rtpfb
        && (packet[0] & 0x1F) == GenericNack;

    /// <summary>
    /// True when the packet's first feedback item names <paramref name="mediaSsrc"/> as its media
    /// source. In SRTCP that field is encrypted, so this tells a decrypted packet from one that
    /// isn't (a random match is a 1 in 2^32 chance).
    /// </summary>
    public static bool IsReadable(ReadOnlySpan<byte> packet, uint mediaSsrc) =>
        packet.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(packet[8..]) == mediaSsrc;

    /// <summary>Adds every sequence number NACKed for <paramref name="mediaSsrc"/> in the packet.</summary>
    public static void ReadLost(ReadOnlySpan<byte> rtcp, uint mediaSsrc, ICollection<ushort> lost)
    {
        var offset = 0;
        while (offset + 4 <= rtcp.Length)
        {
            var header = rtcp[offset..];
            if (header[0] >> 6 != 2)
            {
                return;
            }

            var length = (BinaryPrimitives.ReadUInt16BigEndian(header[2..]) + 1) * 4;
            if (offset + length > rtcp.Length)
            {
                return; // not RTCP any more: the old SRTCP trailer after an in-place decrypt
            }

            var item = rtcp.Slice(offset, length);
            if (item[1] == Rtpfb && (item[0] & 0x1F) == GenericNack && length >= 16
                && BinaryPrimitives.ReadUInt32BigEndian(item[8..]) == mediaSsrc)
            {
                // Each FCI: a lost packet (PID) and a bitmask of the 16 after it (BLP).
                for (var fci = 12; fci + 4 <= length; fci += 4)
                {
                    var pid = BinaryPrimitives.ReadUInt16BigEndian(item[fci..]);
                    var blp = BinaryPrimitives.ReadUInt16BigEndian(item[(fci + 2)..]);
                    lost.Add(pid);
                    for (var bit = 0; bit < 16; bit++)
                    {
                        if ((blp & (1 << bit)) != 0)
                        {
                            lost.Add((ushort)(pid + bit + 1));
                        }
                    }
                }
            }

            offset += length;
        }
    }
}
