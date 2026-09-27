using System.Text;
using System.Text.Json;

namespace GlassesRemote.Server.Protocol;

/// <summary>
/// Strict parser for messages from the glasses. Every message must be a small
/// JSON object with a known <c>type</c> and exactly the properties that type
/// allows, with values of the right kind. Numbers are clamped to safe ranges,
/// text is capped and flattened. Anything else is rejected, and nothing in
/// here ever turns text into a command.
/// </summary>
public static class ControlProtocol
{
    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxTextLength = 500;
    public const int MaxSdpLength = 12 * 1024;
    public const int MaxCandidateLength = 1024;
    public const int MaxTokenLength = 128;
    public const int MaxScrollPerMessage = 1200;
    public const int MaxRegionCoordinate = 32_768;
    public const int MaxAppSlot = 9;
    public const double MaxStatsValue = 1e9;

    /// <summary>What the glasses may report in a <c>stats</c> message: numbers (or null) only.</summary>
    public static readonly string[] ClientStatsFields =
    [
        "e2eMs", "e2eMaxMs", "arrivalMs", "arrivalMaxMs", "playoutMs", "playoutMaxMs", "slowestKb",
        "clockErrorMs", "framesShown", "fps", "jitterBufferMs", "decodeMs", "kbps", "lost", "lostTotal",
        "nacks", "plis", "freezes", "dropped", "keyframes",
        // How the glasses reach the network: type codes (client mediaStats.ts NETWORK_CODES:
        // 1 wifi, 2 cellular, 3 bluetooth, ...), the browser's bandwidth estimate, the UDP round trip.
        "netType", "iceNetType", "downlinkMbps", "rttMs",
        // The PC's sound as received: Opus kbit/s, packets lost in the second, how much of the
        // second was made up by the decoder (lost or late sound), the audio jitter buffer.
        "audioKbps", "audioLost", "audioConcealedMs", "audioBufferMs",
    ];

    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4 };

    private static readonly Dictionary<string, KeyCommand> Keys = new(StringComparer.Ordinal)
    {
        ["Enter"] = KeyCommand.Enter,
        ["Escape"] = KeyCommand.Escape,
        ["Tab"] = KeyCommand.Tab,
        ["Backspace"] = KeyCommand.Backspace,
        ["Ctrl+C"] = KeyCommand.CtrlC,
        ["Ctrl+V"] = KeyCommand.CtrlV,
        ["Alt+Tab"] = KeyCommand.AltTab,
        ["Win+Shift+Left"] = KeyCommand.WinShiftLeft,
        ["Win+Shift+Right"] = KeyCommand.WinShiftRight,
    };

    private static readonly Dictionary<string, ViewMode> Modes = new(StringComparer.Ordinal)
    {
        ["overview"] = ViewMode.Overview,
        ["view"] = ViewMode.View,
        ["pointer"] = ViewMode.Pointer,
        ["scroll"] = ViewMode.Scroll,
        ["type"] = ViewMode.Type,
    };

    /// <summary>The wire name of a mode ("pointer", ...), as the client sends and expects it.</summary>
    public static string ModeName(ViewMode mode) => Modes.First(pair => pair.Value == mode).Key;

    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out ControlMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (utf8Json.Length == 0 || utf8Json.Length > MaxMessageBytes)
        {
            error = "size";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                error = "shape";
                return false;
            }

            message = typeElement.GetString() switch
            {
                "authenticate" => ParseAuthenticate(root),
                "resume" => ParseResume(root),
                "rtcAnswer" => ParseRtcAnswer(root),
                "iceCandidate" => ParseIceCandidate(root),
                "setMode" => ParseSetMode(root),
                "setRegion" => ParseSetRegion(root),
                "move" => ParseMove(root),
                "click" => ParseClick(root),
                "mouseButton" => ParseMouseButton(root),
                "scroll" => ParseScroll(root),
                "typeText" => ParseTypeText(root),
                "key" => ParseKey(root),
                "ping" => ParsePing(root),
                "switchApp" => ParseSwitchApp(root),
                "setAudio" => ParseSetAudio(root),
                "stats" => ParseClientStats(root),
                _ => null,
            };
        }
        catch (JsonException)
        {
            error = "json";
            return false;
        }

        if (message is null)
        {
            error = "invalid";
            return false;
        }

        return true;
    }

    public static bool TryParse(string json, out ControlMessage? message, out string? error) =>
        TryParse(Encoding.UTF8.GetBytes(json), out message, out error);

    private static ControlMessage? ParseAuthenticate(JsonElement e) =>
        Only(e, "token", "target") && Str(e, "token", MaxTokenLength, out var token) && Target(e, out var target)
            ? new AuthenticateMessage(token, target)
            : null;

    private static ControlMessage? ParseResume(JsonElement e) =>
        Only(e, "token", "target") && Str(e, "token", MaxTokenLength, out var token) && Target(e, out var target)
            ? new ResumeMessage(token, target)
            : null;

    /// <summary>Optional "target": "pc" (the default) or "phone".</summary>
    private static bool Target(JsonElement e, out SessionTarget target)
    {
        target = SessionTarget.Pc;
        if (!e.TryGetProperty("target", out _))
        {
            return true;
        }

        if (!Str(e, "target", 8, out var name))
        {
            return false;
        }

        switch (name)
        {
            case "pc":
                return true;
            case "phone":
                target = SessionTarget.Phone;
                return true;
            default:
                return false;
        }
    }

    private static ControlMessage? ParseRtcAnswer(JsonElement e) =>
        Only(e, "sdp") && Str(e, "sdp", MaxSdpLength, out var sdp)
            ? new RtcAnswerMessage(sdp)
            : null;

    private static ControlMessage? ParseIceCandidate(JsonElement e) => ParseIce(e);

    /// <summary>An ICE candidate's fields ("candidate", "sdpMid", "sdpMLineIndex"); shared with the companion's parser.</summary>
    internal static IceCandidateMessage? ParseIce(JsonElement e)
    {
        if (!Only(e, "candidate", "sdpMid", "sdpMLineIndex") || !Str(e, "candidate", MaxCandidateLength, out var candidate))
        {
            return null;
        }

        string? mid = null;
        if (e.TryGetProperty("sdpMid", out var midElement) && midElement.ValueKind != JsonValueKind.Null)
        {
            if (!Str(e, "sdpMid", 32, out var midValue))
            {
                return null;
            }
            mid = midValue;
        }

        var index = 0;
        if (e.TryGetProperty("sdpMLineIndex", out var indexElement) && indexElement.ValueKind != JsonValueKind.Null)
        {
            if (!Int(e, "sdpMLineIndex", out index) || index is < 0 or > 16)
            {
                return null;
            }
        }

        return new IceCandidateMessage(candidate, mid, index);
    }

    private static ControlMessage? ParseSetMode(JsonElement e) =>
        Only(e, "mode") && Str(e, "mode", 16, out var mode) && Modes.TryGetValue(mode, out var parsed)
            ? new SetModeMessage(parsed)
            : null;

    private static ControlMessage? ParseSetRegion(JsonElement e)
    {
        if (!Only(e, "x", "y", "width", "height")
            || !Int(e, "x", out var x) || !Int(e, "y", out var y)
            || !Int(e, "width", out var width) || !Int(e, "height", out var height))
        {
            return null;
        }

        // Coarse sanity bounds only; RegionMath clamps to the real monitor.
        const int max = MaxRegionCoordinate;
        return new SetRegionMessage(Math.Clamp(x, -max, max), Math.Clamp(y, -max, max),
            Math.Clamp(width, 1, max), Math.Clamp(height, 1, max));
    }

    private static ControlMessage? ParseMove(JsonElement e) =>
        Only(e, "x", "y") && Num(e, "x", out var x) && Num(e, "y", out var y)
            ? new MoveMessage(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1))
            : null;

    private static ControlMessage? ParseClick(JsonElement e) =>
        Only(e, "button") && Str(e, "button", 8, out var button) && button == "left"
            ? new ClickMessage(MouseButton.Left)
            : null;

    private static ControlMessage? ParseMouseButton(JsonElement e) =>
        Only(e, "button", "down") && Str(e, "button", 8, out var button) && button == "left" && Bool(e, "down", out var down)
            ? new MouseButtonMessage(MouseButton.Left, down)
            : null;

    private static ControlMessage? ParseScroll(JsonElement e) =>
        Only(e, "dy") && Int(e, "dy", out var dy)
            ? new ScrollMessage(Math.Clamp(dy, -MaxScrollPerMessage, MaxScrollPerMessage))
            : null;

    private static ControlMessage? ParseTypeText(JsonElement e)
    {
        if (!Only(e, "text") || !Str(e, "text", MaxTextLength, out var text))
        {
            return null;
        }

        var flattened = FlattenText(text);
        return flattened.Length == 0 ? null : new TypeTextMessage(flattened);
    }

    private static ControlMessage? ParseKey(JsonElement e) =>
        Only(e, "key") && Str(e, "key", 32, out var key) && Keys.TryGetValue(key, out var command)
            ? new KeyMessage(command)
            : null;

    private static ControlMessage? ParseSwitchApp(JsonElement e) =>
        Only(e, "slot") && Int(e, "slot", out var slot) && slot is >= 1 and <= MaxAppSlot
            ? new SwitchAppMessage(slot)
            : null;

    private static ControlMessage? ParseSetAudio(JsonElement e) =>
        Only(e, "enabled") && Bool(e, "enabled", out var enabled) ? new SetAudioMessage(enabled) : null;

    private static ControlMessage? ParseClientStats(JsonElement e)
    {
        if (!Only(e, ClientStatsFields))
        {
            return null;
        }

        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var name in ClientStatsFields)
        {
            if (!e.TryGetProperty(name, out var p))
            {
                continue;
            }
            if (p.ValueKind == JsonValueKind.Null)
            {
                values[name] = null;
            }
            else if (Num(e, name, out var value))
            {
                values[name] = Math.Round(Math.Clamp(value, -MaxStatsValue, MaxStatsValue), 1);
            }
            else
            {
                return null;
            }
        }
        return new ClientStatsMessage(values);
    }

    private static ControlMessage? ParsePing(JsonElement e) =>
        Only(e, "t") && Num(e, "t", out var t) ? new PingMessage(t) : null;

    /// <summary>
    /// Line breaks and tabs become spaces so voice text can never press Enter or Tab;
    /// other control characters are dropped.
    /// </summary>
    internal static string FlattenText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t' or LineSeparator or ParagraphSeparator)
            {
                sb.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    internal static bool Only(JsonElement e, params string[] allowed)
    {
        foreach (var property in e.EnumerateObject())
        {
            if (property.Name != "type" && Array.IndexOf(allowed, property.Name) < 0)
            {
                return false;
            }
        }
        return true;
    }

    internal static bool Str(JsonElement e, string name, int maxLength, out string value)
    {
        value = "";
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = p.GetString()!;
        return value.Length <= maxLength;
    }

    internal static bool Bool(JsonElement e, string name, out bool value)
    {
        value = false;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = p.GetBoolean();
        return true;
    }

    internal static bool Num(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.Number
               && p.TryGetDouble(out value)
               && double.IsFinite(value);
    }

    internal static bool Int(JsonElement e, string name, out int value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (p.TryGetInt32(out value))
        {
            return true;
        }

        // Out-of-range integers saturate instead of failing, so clamping still applies.
        if (p.TryGetDouble(out var d) && double.IsFinite(d) && Math.Floor(d) == d)
        {
            value = d > 0 ? int.MaxValue : int.MinValue;
            return true;
        }

        return false;
    }
}
