using System.Net;
using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>Glasses at home get the LAN address first; everyone else only the public one.</summary>
public sealed class MediaPathsTests
{
    private static readonly IPAddress Public = IPAddress.Parse("203.0.113.45");
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.114");

    private const string Offer =
        "v=0\r\n" +
        "m=video 9 UDP/TLS/RTP/SAVP 102\r\n" +
        "a=mid:0\r\n" +
        "a=candidate:1 1 udp 2113937151 192.168.1.114 50000 typ host generation 0\r\n" +
        "a=rtpmap:102 H264/90000\r\n";

    [Fact]
    public void Glasses_behind_the_same_router_are_at_home()
    {
        Assert.True(MediaPaths.IsHome(Public, Public));
        Assert.True(MediaPaths.IsHome(Public.MapToIPv6(), Public));
        Assert.False(MediaPaths.IsHome(IPAddress.Parse("187.15.162.141"), Public));
        Assert.False(MediaPaths.IsHome(Public, null));
    }

    [Theory]
    [InlineData("192.168.1.30", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("187.15.162.141", false)]
    [InlineData("::ffff:192.168.1.30", true)]
    public void Private_addresses_mean_the_lan(string address, bool lan) =>
        Assert.Equal(lan, MediaPaths.IsLan(IPAddress.Parse(address)));

    [Fact]
    public void At_home_the_lan_address_comes_first_and_ranks_above_the_public_one()
    {
        var sdp = SdpCandidates.Rewrite(Offer, Public, 50000, includeLanCandidates: false, lanIp: Lan);

        var candidates = sdp.Split("\r\n").Where(l => l.StartsWith("a=candidate:")).ToList();
        Assert.Equal(2, candidates.Count);
        Assert.StartsWith("a=candidate:lan1 1 udp 2130706431 192.168.1.114 50000 typ host", candidates[0]);
        Assert.StartsWith("a=candidate:pub1 1 udp 2130706175 203.0.113.45 50000 typ host", candidates[1]);
    }

    [Fact]
    public void Away_from_home_only_the_public_address_is_offered()
    {
        var sdp = SdpCandidates.Rewrite(Offer, Public, 50000, includeLanCandidates: false);

        var candidates = sdp.Split("\r\n").Where(l => l.StartsWith("a=candidate:")).ToList();
        Assert.Equal(["a=candidate:pub1 1 udp 2130706175 203.0.113.45 50000 typ host generation 0"], candidates);
        Assert.DoesNotContain("192.168.1.114", sdp);
    }
}
