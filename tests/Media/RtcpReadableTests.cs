using System.Buffers.Binary;
using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>Tells RTCP that SIPSorcery decrypted in place from RTCP it couldn't.</summary>
public sealed class RtcpReadableTests
{
    private const uint Ours = 0xCAFEBABE;

    /// <summary>An RTCP item: header, sender SSRC 1, then <paramref name="third"/> in the first encrypted word.</summary>
    private static byte[] Item(byte type, int count, uint third)
    {
        var packet = new byte[24];
        packet[0] = (byte)(0x80 | count);
        packet[1] = type;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), third);
        return packet;
    }

    [Theory]
    [InlineData(201, 1)] // receiver report with a block about our stream
    [InlineData(205, 1)] // NACK
    [InlineData(205, 15)] // transport-cc
    [InlineData(206, 1)] // PLI
    public void Feedback_naming_our_stream_is_decrypted_and_anything_else_there_is_not(byte type, int count)
    {
        Assert.True(RtcpReadable.Decrypted(Item(type, count, Ours), Ours));
        Assert.False(RtcpReadable.Decrypted(Item(type, count, 0x5EC12E7), Ours));
    }

    [Fact]
    public void Reports_on_our_audio_stream_count_as_ours_too()
    {
        const uint audio = 0xA0D10;
        Assert.True(RtcpReadable.Decrypted(Item(201, 1, audio), Ours, audio));
        Assert.False(RtcpReadable.Decrypted(Item(201, 1, 0x5EC12E7), Ours, audio));
    }

    [Theory]
    [InlineData(201, 0)] // an empty receiver report
    [InlineData(206, 15)] // REMB: 0 in the media SSRC field
    [InlineData(206, 4)] // FIR: 0 there too
    [InlineData(202, 1)] // SDES
    public void Items_that_carry_no_stream_field_cannot_tell(byte type, int count)
    {
        Assert.Null(RtcpReadable.Decrypted(Item(type, count, 0), Ours));
    }

    [Fact]
    public void Rtp_and_short_packets_are_not_rtcp()
    {
        var rtp = Item(102, 0, Ours); // H.264 payload type 102, marker off
        Assert.False(RtcpReadable.IsRtcp(rtp));
        Assert.Null(RtcpReadable.Decrypted(rtp, Ours));
        Assert.Null(RtcpReadable.Decrypted(Item(201, 1, Ours).AsSpan(0, 10), Ours));
    }
}
