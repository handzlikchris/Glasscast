using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Protocol;

namespace GlassesRemote.Server.Sessions;

public enum HandleResult
{
    Handled,

    /// <summary>Valid message, but not meaningful in the current mode; ignored.</summary>
    IgnoredForMode,

    RegionChanged,
}

/// <summary>
/// Applies validated control messages for one session: tracks the mode and the
/// capture region, and turns pointer/scroll/type messages into input. Each
/// action is only honoured in its own mode, so a pinch-drag can never mean two
/// things at once and text can't be typed from Pointer mode by accident.
/// </summary>
public sealed class InputController
{
    private readonly IInputInjector _input;
    private readonly RegionStore _store;
    private readonly PixelSize _monitor;
    private readonly object _gate = new();
    private (int X, int Y)? _cursor;
    private bool _buttonDown;

    public InputController(IInputInjector input, RegionStore store, PixelSize monitor, CaptureRegion? savedRegion)
    {
        _input = input;
        _store = store;
        _monitor = monitor;
        Region = RegionMath.Clamp(savedRegion ?? RegionMath.Default(monitor), monitor);
    }

    /// <summary>Sessions start in Pointer mode: that's what the glasses are mostly used for.</summary>
    public ViewMode Mode { get; private set; } = ViewMode.Pointer;

    public CaptureRegion Region { get; private set; }

    public PixelSize Monitor => _monitor;

    /// <summary>The monitor rectangle the video should show right now.</summary>
    public PixelRect CurrentSource
    {
        get
        {
            lock (_gate)
            {
                return Mode == ViewMode.Overview
                    ? new PixelRect(0, 0, _monitor.Width, _monitor.Height)
                    : new PixelRect(Region.X, Region.Y, Region.Width, Region.Height);
            }
        }
    }

    public HandleResult Handle(ControlMessage message)
    {
        lock (_gate)
        {
            switch (message)
            {
                case SetModeMessage m:
                    Mode = m.Mode;
                    if (Mode != ViewMode.Pointer)
                    {
                        ReleaseButtonLocked();
                    }
                    return HandleResult.Handled;

                case SetRegionMessage m:
                    Region = RegionMath.Clamp(new CaptureRegion(m.X, m.Y, m.Width, m.Height), _monitor);
                    _store.Save(Region);
                    return HandleResult.RegionChanged;

                case MoveMessage m when Mode == ViewMode.Pointer:
                    var target = RegionMath.ToScreen(Region, m.X, m.Y);
                    _input.MoveTo(target.X, target.Y);
                    _cursor = target;
                    return HandleResult.Handled;

                case ClickMessage m when Mode == ViewMode.Pointer && !_buttonDown:
                    EnsureCursorInRegion();
                    _input.Click(m.Button);
                    return HandleResult.Handled;

                case MouseButtonMessage { Down: true } m when Mode == ViewMode.Pointer && !_buttonDown:
                    EnsureCursorInRegion();
                    _input.Button(m.Button, true);
                    _buttonDown = true;
                    return HandleResult.Handled;

                case MouseButtonMessage { Down: false } when _buttonDown:
                    ReleaseButtonLocked();
                    return HandleResult.Handled;

                case ScrollMessage m when Mode is ViewMode.Pointer or ViewMode.Scroll:
                    EnsureCursorInRegion();
                    // Protocol dy follows browser deltaY (positive = scroll down); Windows wheel is the opposite.
                    _input.Wheel(-m.Dy);
                    return HandleResult.Handled;

                case TypeTextMessage m when Mode == ViewMode.Type:
                    _input.TypeText(m.Text);
                    return HandleResult.Handled;

                case KeyMessage m when Mode == ViewMode.Type:
                    _input.Press(m.Key);
                    return HandleResult.Handled;

                default:
                    return HandleResult.IgnoredForMode;
            }
        }
    }

    /// <summary>Lets go of a held button (the session is ending): nothing stays pressed on the PC.</summary>
    public void ReleaseButton()
    {
        lock (_gate)
        {
            ReleaseButtonLocked();
        }
    }

    private void ReleaseButtonLocked()
    {
        if (!_buttonDown)
        {
            return;
        }
        _buttonDown = false;
        _input.Button(MouseButton.Left, false);
    }

    /// <summary>Wheel and click go to whatever is under the cursor, so make sure that's inside the region.</summary>
    private void EnsureCursorInRegion()
    {
        var inside = _cursor is { } c
                     && c.X >= Region.X && c.X < Region.X + Region.Width
                     && c.Y >= Region.Y && c.Y < Region.Y + Region.Height;
        if (inside)
        {
            return;
        }

        var centre = RegionMath.ToScreen(Region, 0.5, 0.5);
        _input.MoveTo(centre.X, centre.Y);
        _cursor = centre;
    }
}
