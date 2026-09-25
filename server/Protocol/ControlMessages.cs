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

/// <summary>A validated message from the glasses. Anything else is rejected before it gets here.</summary>
public abstract record ControlMessage;

public sealed record AuthenticateMessage(string Token) : ControlMessage
{
    // Never print the token, even in debug output.
    public override string ToString() => "AuthenticateMessage { Token = *** }";
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
/// Wheel movement in Windows wheel units (120 per notch), clamped. Positive scrolls
/// down, like a browser's deltaY.
/// </summary>
public sealed record ScrollMessage(int Dy) : ControlMessage;

/// <summary>Text to type. Line breaks are flattened to spaces: text never presses Enter.</summary>
public sealed record TypeTextMessage(string Text) : ControlMessage;

public sealed record KeyMessage(KeyCommand Key) : ControlMessage;

public sealed record PingMessage(double T) : ControlMessage;

/// <summary>Switch to app shortcut <paramref name="Slot"/> (1-based) from the PC's configured list.</summary>
public sealed record SwitchAppMessage(int Slot) : ControlMessage;
