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
        Assert.Equal(new CompanionOfferMessage("v=0", Mac), Parse($$"""{"type":"rtcOffer","sdp":"v=0","mac":"{{Mac}}"}"""));
        Assert.Equal(new CompanionPairKeyMessage(Key), Parse($$"""{"type":"pairKey","key":"{{Key}}"}"""));
        Assert.IsType<CompanionPairedMessage>(Parse("""{"type":"paired"}"""));
        Assert.IsType<CompanionPairFailedMessage>(Parse("""{"type":"pairFailed"}"""));
        Assert.Equal(new CompanionChallengeMessage(Nonce, Mac), Parse($$"""{"type":"challenge","nonce":"{{Nonce}}","mac":"{{Mac}}"}"""));
        Assert.IsType<CompanionAuthFailedMessage>(Parse("""{"type":"authFailed"}"""));

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
    // The offer must carry its MAC, and keys, nonces and MACs are base64url of exact sizes.
    [InlineData("""{"type":"rtcOffer","sdp":"v=0"}""")]
    [InlineData("""{"type":"pairKey","key":"short"}""")]
    [InlineData("""{"type":"challenge","nonce":"AAAAAAAAAAAAAAAAAAAAA+","mac":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")]
    [InlineData("""{"type":"paired","token":"x"}""")]
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

    [Fact]
    public void Glasses_ask_for_the_phone_with_a_bare_message()
    {
        Assert.True(ControlProtocol.TryParse("""{"type":"phone"}""", out var message, out _));
        Assert.IsType<ConnectPhoneMessage>(message);
        Assert.False(ControlProtocol.TryParse("""{"type":"phone","token":"t"}""", out _, out _));
    }

    [Theory]
    // The session target went with phone mode's own pairing: a token is only ever for the PC.
    [InlineData("""{"type":"authenticate","token":"t","target":"phone"}""")]
    [InlineData("""{"type":"resume","token":"t","target":"pc"}""")]
    public void Tokens_no_longer_carry_a_target(string json)
    {
        Assert.False(ControlProtocol.TryParse(json, out _, out _));
    }

    internal const string Mac = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    internal const string Nonce = "AAAAAAAAAAAAAAAAAAAAAA";
    internal const string Id = "AAAAAAAAAAAAAAAA";
    internal static readonly string Key = "B" + new string('A', 86);

    private static CompanionMessage? Parse(string json)
    {
        Assert.True(CompanionProtocol.TryParse(json, out var message, out var error), error);
        return message;
    }
}
