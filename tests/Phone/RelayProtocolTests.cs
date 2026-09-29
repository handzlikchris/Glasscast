using GlassesRemote.Server.Phone;
using static GlassesRemote.Server.Tests.Phone.CompanionProtocolTests;

namespace GlassesRemote.Server.Tests.Phone;

/// <summary>The glasses' side of a phone relay: pairing, authentication and signalling, nothing else.</summary>
public sealed class RelayProtocolTests
{
    [Fact]
    public void Parses_every_relay_message()
    {
        Assert.Equal(new PairStartSignal(Mac), Parse($$"""{"type":"pairStart","commit":"{{Mac}}"}"""));
        Assert.Equal(new PairRevealSignal(Key), Parse($$"""{"type":"pairReveal","key":"{{Key}}"}"""));
        Assert.Equal(new HelloSignal(Id, Nonce), Parse($$"""{"type":"hello","id":"{{Id}}","nonce":"{{Nonce}}"}"""));
        Assert.Equal(new ProofSignal(Mac), Parse($$"""{"type":"proof","mac":"{{Mac}}"}"""));
        Assert.Equal(new AnswerSignal("v=0\r\n", Mac), Parse($$"""{"type":"rtcAnswer","sdp":"v=0\r\n","mac":"{{Mac}}"}"""));
        Assert.Equal(new RelayPingSignal(3), Parse("""{"type":"ping","t":3}"""));
        var ice = Assert.IsType<GlassesIceSignal>(Parse("""{"type":"iceCandidate","candidate":"candidate:1 1 udp 1 10.0.0.2 5000 typ host","sdpMid":"0","sdpMLineIndex":0}"""));
        Assert.Equal(0, ice.Candidate.SdpMLineIndex);
    }

    [Theory]
    // Wrong sizes or alphabets.
    [InlineData("""{"type":"pairStart","commit":"abc"}""")]
    [InlineData("""{"type":"pairStart","commit":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}""")]
    [InlineData("""{"type":"hello","id":"AAAAAAAAAAAAAAAA","nonce":"AAAAAAAAAAAAAAAAAAAAA"}""")]
    [InlineData("""{"type":"hello","id":"AAAAAAAAAAAAAAA/","nonce":"AAAAAAAAAAAAAAAAAAAAAA"}""")]
    // An answer without its MAC, and extra properties.
    [InlineData("""{"type":"rtcAnswer","sdp":"v=0"}""")]
    [InlineData("""{"type":"ping","t":1,"x":2}""")]
    // A PC session's messages, and the phone's input: never through the PC.
    [InlineData("""{"type":"authenticate","token":"t"}""")]
    [InlineData("""{"type":"click","button":"left"}""")]
    [InlineData("""{"type":"tap","x":0.5,"y":0.5}""")]
    [InlineData("""{"type":"typeText","text":"hi"}""")]
    [InlineData("""not json""")]
    public void Rejects_anything_else(string json)
    {
        Assert.False(RelayProtocol.TryParse(json, out _, out _));
    }

    private static GlassesSignal? Parse(string json)
    {
        Assert.True(RelayProtocol.TryParse(json, out var message, out var error), error);
        return message;
    }
}
