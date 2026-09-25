using GlassesRemote.Server.Protocol;

namespace GlassesRemote.Server.Tests.Protocol;

// Brief tests 11 and 12: invalid and oversized messages are rejected;
// coordinates and text lengths are clamped.
public sealed class ControlProtocolTests
{
    private static T Parse<T>(string json) where T : ControlMessage
    {
        Assert.True(ControlProtocol.TryParse(json, out var message, out var error), $"rejected: {error}");
        return Assert.IsType<T>(message);
    }

    private static void Rejected(string json) =>
        Assert.False(ControlProtocol.TryParse(json, out _, out _), $"accepted: {json}");

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"move\"")]
    [InlineData("{}")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":\"shell\",\"cmd\":\"calc\"}")]
    [InlineData("{\"type\":\"MOVE\",\"x\":0.5,\"y\":0.5}")]
    public void Rejects_malformed_and_unknown_messages(string json) => Rejected(json);

    [Fact]
    public void Rejects_unexpected_properties()
    {
        Rejected("{\"type\":\"click\",\"button\":\"left\",\"x\":1}");
        Rejected("{\"type\":\"ping\",\"t\":1,\"extra\":true}");
    }

    [Fact]
    public void Rejects_oversized_messages()
    {
        var text = new string('a', ControlProtocol.MaxMessageBytes);
        Rejected($"{{\"type\":\"typeText\",\"text\":\"{text}\"}}");
    }

    [Fact]
    public void Rejects_deeply_nested_json()
    {
        Rejected("{\"type\":\"ping\",\"t\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}");
    }

    [Fact]
    public void Move_is_clamped_to_the_view()
    {
        var move = Parse<MoveMessage>("{\"type\":\"move\",\"x\":-3,\"y\":7.5}");
        Assert.Equal(0, move.X);
        Assert.Equal(1, move.Y);
    }

    [Theory]
    [InlineData("{\"type\":\"move\",\"x\":\"0.5\",\"y\":0.5}")]
    [InlineData("{\"type\":\"move\",\"x\":0.5}")]
    [InlineData("{\"type\":\"move\",\"x\":null,\"y\":0.5}")]
    public void Move_requires_two_numbers(string json) => Rejected(json);

    [Fact]
    public void Scroll_is_clamped_per_message()
    {
        Assert.Equal(1200, Parse<ScrollMessage>("{\"type\":\"scroll\",\"dy\":99999999999}").Dy);
        Assert.Equal(-1200, Parse<ScrollMessage>("{\"type\":\"scroll\",\"dy\":-5000}").Dy);
        Assert.Equal(-120, Parse<ScrollMessage>("{\"type\":\"scroll\",\"dy\":-120}").Dy);
        Rejected("{\"type\":\"scroll\",\"dy\":1.5}");
    }

    [Fact]
    public void Text_longer_than_the_cap_is_rejected()
    {
        Parse<TypeTextMessage>($"{{\"type\":\"typeText\",\"text\":\"{new string('a', ControlProtocol.MaxTextLength)}\"}}");
        Rejected($"{{\"type\":\"typeText\",\"text\":\"{new string('a', ControlProtocol.MaxTextLength + 1)}\"}}");
    }

    [Fact]
    public void Text_never_carries_line_breaks_or_control_characters()
    {
        var message = Parse<TypeTextMessage>("{\"type\":\"typeText\",\"text\":\"rm -rf /\\nsecond\\r\\nline\\u0007\\tend\"}");
        // \n and \r\n become spaces, the BEL is dropped, the tab becomes a space.
        Assert.Equal("rm -rf / second  line end", message.Text);
        Assert.DoesNotContain('\n', message.Text);
        Assert.DoesNotContain('\r', message.Text);
    }

    [Fact]
    public void Blank_text_is_rejected()
    {
        Rejected("{\"type\":\"typeText\",\"text\":\"\\n\\n  \"}");
    }

    [Fact]
    public void Switch_app_takes_a_slot_number_only()
    {
        Assert.True(ControlProtocol.TryParse("{\"type\":\"switchApp\",\"slot\":2}", out var message, out _));
        Assert.Equal(new SwitchAppMessage(2), message);
    }

    [Theory]
    [InlineData("{\"type\":\"switchApp\",\"slot\":0}")]
    [InlineData("{\"type\":\"switchApp\",\"slot\":10}")]
    [InlineData("{\"type\":\"switchApp\",\"slot\":\"1\"}")]
    [InlineData("{\"type\":\"switchApp\",\"slot\":1.5}")]
    [InlineData("{\"type\":\"switchApp\",\"name\":\"chrome\"}")]
    [InlineData("{\"type\":\"switchApp\",\"slot\":1,\"process\":\"cmd\"}")]
    public void Switch_app_rejects_anything_but_a_small_slot(string json) => Rejected(json);

    [Theory]
    [InlineData("Enter", KeyCommand.Enter)]
    [InlineData("Ctrl+V", KeyCommand.CtrlV)]
    [InlineData("Win+Shift+Left", KeyCommand.WinShiftLeft)]
    public void Keys_come_from_an_allowlist(string key, KeyCommand expected)
    {
        Assert.Equal(expected, Parse<KeyMessage>($"{{\"type\":\"key\",\"key\":\"{key}\"}}").Key);
    }

    [Theory]
    [InlineData("F4")]
    [InlineData("Alt+F4")]
    [InlineData("Win+R")]
    [InlineData("enter")]
    public void Other_keys_are_rejected(string key) => Rejected($"{{\"type\":\"key\",\"key\":\"{key}\"}}");

    [Fact]
    public void Region_values_are_coarsely_bounded()
    {
        var region = Parse<SetRegionMessage>("{\"type\":\"setRegion\",\"x\":-99999999,\"y\":10,\"width\":0,\"height\":600}");
        Assert.Equal(-ControlProtocol.MaxRegionCoordinate, region.X);
        Assert.Equal(1, region.Width);
    }

    [Fact]
    public void Stats_carry_known_numbers_rounded_and_bounded()
    {
        var stats = Parse<ClientStatsMessage>(
            "{\"type\":\"stats\",\"e2eMs\":123.456,\"jitterBufferMs\":null,\"lostTotal\":1e15,\"arrivalMs\":-12}");
        Assert.Equal(123.5, stats.Values["e2eMs"]);
        Assert.Null(stats.Values["jitterBufferMs"]);
        Assert.Equal(ControlProtocol.MaxStatsValue, stats.Values["lostTotal"]);
        Assert.Equal(-12, stats.Values["arrivalMs"]);
        Assert.False(stats.Values.ContainsKey("plis"));
    }

    [Theory]
    [InlineData("{\"type\":\"stats\",\"e2eMs\":\"12\"}")]
    [InlineData("{\"type\":\"stats\",\"e2eMs\":[12]}")]
    [InlineData("{\"type\":\"stats\",\"e2eMs\":true}")]
    [InlineData("{\"type\":\"stats\",\"note\":\"hello\"}")]
    [InlineData("{\"type\":\"stats\",\"e2eMs\":1,\"token\":\"x\"}")]
    public void Stats_reject_anything_but_known_numbers(string json) => Rejected(json);

    [Fact]
    public void Mode_must_be_known()
    {
        Assert.Equal(ViewMode.Pointer, Parse<SetModeMessage>("{\"type\":\"setMode\",\"mode\":\"pointer\"}").Mode);
        Rejected("{\"type\":\"setMode\",\"mode\":\"admin\"}");
    }

    [Fact]
    public void Authenticate_token_is_length_limited_and_hidden_from_ToString()
    {
        var auth = Parse<AuthenticateMessage>("{\"type\":\"authenticate\",\"token\":\"abc\"}");
        Assert.DoesNotContain("abc", auth.ToString());
        Rejected($"{{\"type\":\"authenticate\",\"token\":\"{new string('a', 200)}\"}}");
    }

    [Fact]
    public void Ice_candidate_accepts_browser_shape()
    {
        var c = Parse<IceCandidateMessage>(
            "{\"type\":\"iceCandidate\",\"candidate\":\"candidate:1 1 udp 1 1.2.3.4 5 typ host\",\"sdpMid\":\"0\",\"sdpMLineIndex\":0}");
        Assert.Equal("0", c.SdpMid);
        Parse<IceCandidateMessage>("{\"type\":\"iceCandidate\",\"candidate\":\"x\",\"sdpMid\":null,\"sdpMLineIndex\":null}");
        Rejected("{\"type\":\"iceCandidate\",\"candidate\":\"x\",\"sdpMLineIndex\":99}");
    }
}
