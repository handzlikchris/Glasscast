using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Protocol;

namespace GlassesRemote.Server.Tests.Phone;

public sealed class CompanionProtocolTests
{
    [Fact]
    public void Parses_every_companion_message()
    {
        Assert.Equal(new CompanionPairMessage("Galaxy S25"), Parse("""{"type":"pair","name":"Galaxy S25"}"""));
        Assert.IsType<CompanionAuthMessage>(Parse("""{"type":"auth","token":"abc"}"""));
        Assert.Equal(new CompanionPingMessage(12.5), Parse("""{"type":"ping","t":12.5}"""));
        Assert.Equal(new CompanionStateMessage(PhoneState.Asking), Parse("""{"type":"sessionState","state":"asking"}"""));
        Assert.Equal(new CompanionStateMessage(PhoneState.Declined), Parse("""{"type":"sessionState","state":"declined"}"""));
        Assert.Equal(new CompanionStateMessage(PhoneState.Live), Parse("""{"type":"sessionState","state":"live"}"""));
        Assert.Equal(new CompanionStateMessage(PhoneState.Ended), Parse("""{"type":"sessionState","state":"ended"}"""));
        Assert.Equal(new CompanionOfferMessage("v=0"), Parse("""{"type":"rtcOffer","sdp":"v=0"}"""));

        var ice = Assert.IsType<CompanionIceMessage>(Parse("""{"type":"iceCandidate","candidate":"candidate:1 1 udp 1 10.0.0.2 5000 typ host","sdpMid":"0","sdpMLineIndex":0}"""));
        Assert.Equal("0", ice.Candidate.SdpMid);
    }

    [Theory]
    [InlineData("""{"type":"pair"}""")]
    [InlineData("""{"type":"pair","name":"   "}""")]
    [InlineData("""{"type":"pair","name":"a name that is far too long for the popup"}""")]
    [InlineData("""{"type":"auth","token":"abc","extra":1}""")]
    [InlineData("""{"type":"sessionState","state":"hacked"}""")]
    [InlineData("""{"type":"rtcOffer","sdp":5}""")]
    [InlineData("""{"type":"iceCandidate","candidate":"x","sdpMLineIndex":99}""")]
    // Input never goes through the PC for the phone.
    [InlineData("""{"type":"tap","x":0.5,"y":0.5}""")]
    [InlineData("""{"type":"typeText","text":"hi"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void Rejects_anything_else(string json)
    {
        Assert.False(CompanionProtocol.TryParse(json, out _, out _));
    }

    [Fact]
    public void Pair_names_are_flattened_to_one_line()
    {
        Assert.Equal(new CompanionPairMessage("Chris phone"), Parse("{\"type\":\"pair\",\"name\":\"Chris\\nphone\"}"));
    }

    [Fact]
    public void Auth_tokens_are_never_printed()
    {
        Assert.DoesNotContain("secret", Parse("""{"type":"auth","token":"secret"}""")!.ToString());
    }

    [Theory]
    [InlineData("""{"type":"authenticate","token":"t"}""", SessionTarget.Pc)]
    [InlineData("""{"type":"authenticate","token":"t","target":"pc"}""", SessionTarget.Pc)]
    [InlineData("""{"type":"authenticate","token":"t","target":"phone"}""", SessionTarget.Phone)]
    [InlineData("""{"type":"resume","token":"t","target":"phone"}""", SessionTarget.Phone)]
    public void Glasses_choose_the_session_target(string json, SessionTarget expected)
    {
        Assert.True(ControlProtocol.TryParse(json, out var message, out _));
        var target = message switch
        {
            AuthenticateMessage a => a.Target,
            ResumeMessage r => r.Target,
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(expected, target);
    }

    [Theory]
    [InlineData("""{"type":"authenticate","token":"t","target":"tv"}""")]
    [InlineData("""{"type":"authenticate","token":"t","target":1}""")]
    [InlineData("""{"type":"resume","token":"t","target":null}""")]
    public void Unknown_targets_are_rejected(string json)
    {
        Assert.False(ControlProtocol.TryParse(json, out _, out _));
    }

    private static CompanionMessage? Parse(string json)
    {
        Assert.True(CompanionProtocol.TryParse(json, out var message, out var error), error);
        return message;
    }
}
