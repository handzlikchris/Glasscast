using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassesRemote.Server.Tests.Media;

public sealed class SdpStreamsTests
{
    private const string Offer =
        "v=0\r\n" +
        "a=group:BUNDLE 0 1\r\n" +
        "m=audio 9 UDP/TLS/RTP/SAVP 111\r\n" +
        "a=mid:0\r\n" +
        "a=rtpmap:111 OPUS/48000/2\r\n" +
        "m=video 9 UDP/TLS/RTP/SAVP 102\r\n" +
        "a=mid:1\r\n" +
        "a=rtpmap:102 H264/90000\r\n";

    [Fact]
    public void Audio_and_video_get_streams_of_their_own_so_nothing_waits_for_lip_sync()
    {
        var sdp = SdpStreams.Separate(Offer);

        Assert.Contains("a=mid:0\r\na=msid:pc-audio audio\r\n", sdp);
        Assert.Contains("a=mid:1\r\na=msid:pc-video video\r\n", sdp);
        Assert.Equal(sdp, SdpStreams.Separate(sdp));
    }

    [Fact]
    public async Task The_real_offer_carries_stereo_opus_with_fec_and_dtx_next_to_the_video()
    {
        var options = new MediaOptions { MediaPort = 0, PublicIp = "203.0.113.7" };
        using var peer = new SipsorceryMediaPeer(options, "H264", NullLogger.Instance, audio: true);

        var sdp = await peer.CreateOfferAsync(offerLan: false);

        Assert.True(peer.CarriesAudio);
        Assert.Contains("m=audio ", sdp);
        Assert.Matches(@"a=rtpmap:111 (?i:opus)/48000/2\r\n", sdp);
        Assert.Contains($"a=fmtp:111 {SipsorceryMediaPeer.OpusParameters}\r\n", sdp);
        Assert.Contains("a=msid:pc-audio audio", sdp);
        Assert.Contains("a=msid:pc-video video", sdp);
        // Feedback stays on the video: audio isn't resent, and its loss is repaired by FEC.
        Assert.Contains("a=rtcp-fb:102 nack\r\n", sdp);
        Assert.DoesNotContain("a=rtcp-fb:111", sdp);
        // One candidate for the bundle, in the first section.
        Assert.Single(sdp.Split("\r\n"), l => l.StartsWith("a=candidate:pub1"));
    }

    [Fact]
    public async Task Without_audio_the_offer_is_video_only()
    {
        using var peer = new SipsorceryMediaPeer(new MediaOptions { MediaPort = 0 }, "H264", NullLogger.Instance);

        var sdp = await peer.CreateOfferAsync(offerLan: false);

        Assert.False(peer.CarriesAudio);
        Assert.DoesNotContain("m=audio", sdp);
    }
}
