using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>H.264 access units become RTP payloads the browser can put back together (RFC 6184).</summary>
public sealed class H264RtpTests
{
    private static readonly byte[] Sps = [0x67, 0x42, 0xE0, 0x1F, 0x8C];
    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];

    private static byte[] Idr(int length)
    {
        var nal = new byte[length];
        nal[0] = 0x65;
        for (var i = 1; i < length; i++)
        {
            nal[i] = (byte)(i % 251 + 1); // no accidental start codes
        }
        return nal;
    }

    private static byte[] AccessUnit(params byte[][] nals) =>
        nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();

    [Fact]
    public void Small_nals_go_one_per_packet_with_the_marker_on_the_last()
    {
        var idr = Idr(900);

        var packets = H264Rtp.Packetize(AccessUnit(Sps, Pps, idr), timestamp: 4242);

        Assert.Equal([Sps, Pps, idr], packets.Select(p => p.Payload));
        Assert.Equal([false, false, true], packets.Select(p => p.Marker));
        Assert.All(packets, p => Assert.Equal(4242u, p.Timestamp));
    }

    [Fact]
    public void A_large_nal_is_split_into_fu_a_fragments_that_reassemble()
    {
        var idr = Idr(3000);

        var packets = H264Rtp.Packetize(AccessUnit(Sps, idr), timestamp: 1);

        var fragments = packets.Skip(1).ToList();
        Assert.Equal(3, fragments.Count); // 2999 bytes after the NAL header: 1200 + 1200 + 599
        Assert.All(fragments, f => Assert.True(f.Payload.Length <= H264Rtp.MaxPayload + 2));
        Assert.All(fragments, f => Assert.Equal((0x65 & 0xE0) | 28, f.Payload[0])); // FU indicator
        Assert.Equal([0x80 | 5, 5, 0x40 | 5], fragments.Select(f => (int)f.Payload[1])); // start, middle, end
        Assert.Equal([false, false, false, true], packets.Select(p => p.Marker));

        var body = fragments.SelectMany(f => f.Payload.Skip(2));
        var nalType = fragments[0].Payload[1] & 0x1F;
        var rebuilt = new[] { (byte)((fragments[0].Payload[0] & 0xE0) | nalType) }.Concat(body).ToArray();
        Assert.Equal(idr, rebuilt);
    }
}
