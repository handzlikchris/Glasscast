using System.Buffers.Binary;
using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>Every packet the glasses NACK is found, from every FCI of every NACK in the packet.</summary>
public sealed class RtcpNackTests
{
    private const uint Ours = 0xCAFEBABE;

    private static byte[] Nack(uint mediaSsrc, params (ushort Pid, ushort Blp)[] fcis)
    {
        var packet = new byte[12 + 4 * fcis.Length];
        packet[0] = 0x81; // V=2, FMT=1
        packet[1] = 205;  // RTPFB
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), mediaSsrc);
        for (var i = 0; i < fcis.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + 4 * i), fcis[i].Pid);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14 + 4 * i), fcis[i].Blp);
        }
        return packet;
    }

    private static byte[] Remb()
    {
        var packet = new byte[24];
        packet[0] = 0x8F;
        packet[1] = 206;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 5);
        return packet;
    }

    private static List<ushort> Lost(byte[] packet)
    {
        var lost = new List<ushort>();
        RtcpNack.ReadLost(packet, Ours, lost);
        return lost;
    }

    [Fact]
    public void Reads_the_lost_packet_and_the_bitmask_after_it()
    {
        Assert.Equal([100, 101, 103, 116], Lost(Nack(Ours, (100, 0b1000_0000_0000_0101))));
    }

    [Fact]
    public void Reads_every_fci_and_every_nack_and_skips_other_items()
    {
        var packet = Nack(Ours, (10, 0), (65535, 1)).Concat(Remb()).Concat(Nack(Ours, (500, 0))).ToArray();

        Assert.Equal([10, 65535, 0, 500], Lost(packet));
    }

    [Fact]
    public void Ignores_nacks_for_other_streams_and_the_srtcp_trailer()
    {
        // After SIPSorcery decrypts in place the old trailer (E+index, auth tag) is still there.
        byte[] trailer = [0x80, 0, 0, 7, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var packet = Nack(0x12345678, (10, 0)).Concat(Nack(Ours, (20, 0))).Concat(trailer).ToArray();

        Assert.Equal([20], Lost(packet));
    }

    [Fact]
    public void Tells_a_standalone_nack_and_whether_it_is_decrypted()
    {
        var nack = Nack(Ours, (1, 0));

        Assert.True(RtcpNack.IsStandalone(nack));
        Assert.True(RtcpReadable.Decrypted(nack, Ours));
        Assert.False(RtcpReadable.Decrypted(nack, 0x12345678));
        Assert.False(RtcpNack.IsStandalone(Remb()));
    }
}
