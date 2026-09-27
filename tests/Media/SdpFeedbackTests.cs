using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

public sealed class SdpFeedbackTests
{
    private const string Offer =
        "v=0\r\n" +
        "m=video 9 UDP/TLS/RTP/SAVP 102\r\n" +
        "a=rtpmap:102 H264/90000\r\n" +
        "a=rtcp-fb:102 transport-cc\r\n" +
        "a=fmtp:102 packetization-mode=1;profile-level-id=42e01f\r\n";

    [Fact]
    public void Offers_retransmission_and_keyframe_requests_for_h264()
    {
        var sdp = SdpFeedback.AddFeedback(Offer);

        Assert.Contains("a=rtpmap:102 H264/90000\r\na=rtcp-fb:102 nack\r\na=rtcp-fb:102 nack pli\r\na=rtcp-fb:102 ccm fir\r\n", sdp);
        Assert.Contains("a=rtcp-fb:102 transport-cc\r\n", sdp);
        // PLIs and NACKs in packets of their own, not hidden in receiver reports next to REMB.
        Assert.Contains("a=rtcp-rsize\r\n", sdp);
        Assert.EndsWith("\r\n", sdp);
    }

    [Fact]
    public void Is_idempotent_and_offers_vp8_keyframe_requests_only()
    {
        var once = SdpFeedback.AddFeedback("v=0\r\na=rtpmap:96 VP8/90000\r\n");
        Assert.Equal(once, SdpFeedback.AddFeedback(once));
        Assert.Contains("a=rtcp-fb:96 nack pli", once);
        Assert.DoesNotContain("a=rtcp-fb:96 nack\r\n", once); // SIPSorcery's VP8 path can't resend
    }

    [Fact]
    public void Leaves_other_payload_types_alone()
    {
        var sdp = SdpFeedback.AddFeedback("v=0\r\na=rtpmap:111 opus/48000/2\r\n");
        Assert.DoesNotContain("rtcp-", sdp);
    }

    [Theory]
    [InlineData(new byte[] { 0x81, 206, 0, 2, 1, 2, 3, 4, 5, 6, 7, 8 }, true)]  // PLI
    [InlineData(new byte[] { 0x84, 206, 0, 4, 1, 2, 3, 4, 5, 6, 7, 8 }, true)]  // FIR
    [InlineData(new byte[] { 0x8F, 206, 0, 4, 1, 2, 3, 4, 5, 6, 7, 8 }, false)] // REMB (AFB)
    [InlineData(new byte[] { 0x81, 201, 0, 7, 1, 2, 3, 4, 5, 6, 7, 8 }, false)] // receiver report first
    [InlineData(new byte[] { 0x8F, 205, 0, 4, 1, 2, 3, 4, 5, 6, 7, 8 }, false)] // transport-cc
    [InlineData(new byte[] { 0x81, 206, 0, 2 }, false)]                          // too short
    public void Standalone_keyframe_requests_are_spotted_from_the_unencrypted_header(byte[] packet, bool expected) =>
        Assert.Equal(expected, SipsorceryMediaPeer.IsStandaloneKeyframeRequest(packet));
}
