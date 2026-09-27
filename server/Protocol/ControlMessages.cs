namespace GlassesRemote.Server.Protocol;

/// <summary>What pinch-drag means right now. Only Overview changes the video source.</summary>
public enum ViewMode
{
    Overview,
    View,
    Pointer,
    Scroll,
    Type,
}

public enum MouseButton
{
    Left,
}

/// <summary>The only keys and shortcuts the glasses may press. Nothing else is typeable as a key.</summary>
public enum KeyCommand
{
    Enter,
    Escape,
    Tab,
    Backspace,
    CtrlC,
    CtrlV,
    AltTab,
    WinShiftLeft,
    WinShiftRight,
}

/// <summary>What a session controls: this PC, or the phone through its companion app.</summary>
public enum SessionTarget
{
    Pc,
    Phone,
}

/// <summary>A validated message from the glasses. Anything else is rejected before it gets here.</summary>
public abstract record ControlMessage;

public sealed record AuthenticateMessage(string Token, SessionTarget Target = SessionTarget.Pc) : ControlMessage
{
    // Never print the token, even in debug output.
    public override string ToString() => $"AuthenticateMessage {{ Token = ***, Target = {Target} }}";
}

/// <summary>First message of a session from remembered glasses: their device token instead of an approval's token.</summary>
public sealed record ResumeMessage(string Token, SessionTarget Target = SessionTarget.Pc) : ControlMessage
{
    // Never print the token, even in debug output.
    public override string ToString() => $"ResumeMessage {{ Token = ***, Target = {Target} }}";
}

public sealed record RtcAnswerMessage(string Sdp) : ControlMessage;

public sealed record IceCandidateMessage(string Candidate, string? SdpMid, int SdpMLineIndex) : ControlMessage;

public sealed record SetModeMessage(ViewMode Mode) : ControlMessage;

/// <summary>Region in primary-monitor pixels. Clamped to the monitor by the server.</summary>
public sealed record SetRegionMessage(int X, int Y, int Width, int Height) : ControlMessage;

/// <summary>Absolute cursor position within the current view, 0..1 on each axis (clamped).</summary>
public sealed record MoveMessage(double X, double Y) : ControlMessage;

public sealed record ClickMessage(MouseButton Button) : ControlMessage;

/// <summary>
/// Presses or releases a mouse button (pinch, hold still, then move: a drag with the button held,
/// to select text or move a window). The PC releases it on its own when the mode changes or the
/// session ends.
/// </summary>
public sealed record MouseButtonMessage(MouseButton Button, bool Down) : ControlMessage;

/// <summary>
/// Wheel movement in Windows wheel units (120 per notch), clamped. Positive scrolls
/// down, like a browser's deltaY.
/// </summary>
public sealed record ScrollMessage(int Dy) : ControlMessage;

/// <summary>Text to type. Line breaks are flattened to spaces: text never presses Enter.</summary>
public sealed record TypeTextMessage(string Text) : ControlMessage;

public sealed record KeyMessage(KeyCommand Key) : ControlMessage;

public sealed record PingMessage(double T) : ControlMessage;

/// <summary>
/// The glasses' own video measurements (Stats panel figures), for the PC's stats log.
/// Only the names in <see cref="ControlProtocol.ClientStatsFields"/>; a null means "not measured".
/// </summary>
public sealed record ClientStatsMessage(IReadOnlyDictionary<string, double?> Values) : ControlMessage;

/// <summary>Switch to app shortcut <paramref name="Slot"/> (1-based) from the PC's configured list.</summary>
public sealed record SwitchAppMessage(int Slot) : ControlMessage;

/// <summary>The glasses' ♪ button: send the PC's sound, or stop capturing and sending it.</summary>
public sealed record SetAudioMessage(bool Enabled) : ControlMessage;
