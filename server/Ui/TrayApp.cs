using System.Drawing.Drawing2D;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Windows;

namespace GlassesRemote.Server.Ui;

/// <summary>
/// The PC side of the POC: tray icon (grey idle, amber pairing, green in session),
/// approve popup, session banner, throttled alert notifications, and a global
/// hotkey that ends the session at once. Runs on the WinForms UI thread;
/// coordinator and alert events are marshalled onto it.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string HotkeyText = "Ctrl+Alt+Shift+X";

    private readonly PairingCoordinator _coordinator;
    private readonly AlertLog _alerts;
    private readonly Action _requestShutdown;
    private readonly Control _marshal = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _terminate;
    private readonly SessionBanner _banner = new(HotkeyText);
    private readonly CastArea _castArea;
    private readonly CastFrame _frame = new();
    private readonly ToolStripMenuItem _showFrame;
    private readonly ToolStripMenuItem _forget;
    private readonly CompanionRegistry _companion;
    private readonly ToolStripMenuItem _forgetPhone;
    private readonly AlertThrottle _throttle = new(TimeProvider.System, TimeSpan.FromMinutes(1));
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 10_000 };
    private readonly TerminateHotkey _hotkey;
    private readonly Icon _idleIcon = TrayIcons.Dot(Color.FromArgb(140, 150, 160));
    private readonly Icon _pendingIcon = TrayIcons.Dot(Color.FromArgb(235, 170, 30));
    private readonly Icon _activeIcon = TrayIcons.Dot(Color.FromArgb(40, 170, 80));

    private ApprovePopup? _popup;
    private ApprovePopup? _phonePopup;
    private AlertsForm? _alertsForm;

    public TrayApp(PairingCoordinator coordinator, AlertLog alerts, CastArea castArea, CompanionRegistry companion,
        Action requestShutdown)
    {
        _coordinator = coordinator;
        _companion = companion;
        _alerts = alerts;
        _castArea = castArea;
        _requestShutdown = requestShutdown;
        _marshal.CreateControl();

        _status = new ToolStripMenuItem("Idle – waiting for glasses") { Enabled = false };
        _terminate = new ToolStripMenuItem($"End session ({HotkeyText})", null, (_, _) => _coordinator.TerminateActiveSession())
        {
            Enabled = false,
        };
        _showFrame = new ToolStripMenuItem("Show cast area on screen") { Checked = true, CheckOnClick = true };
        _showFrame.CheckedChanged += (_, _) => UpdateFrame(_castArea.Current);
        _forget = new ToolStripMenuItem("Forget remembered glasses", null, (_, _) => _coordinator.ForgetDevice("forgotten from the tray"));
        UpdateForget(_coordinator.RememberedDeviceExpiresAt);
        _forgetPhone = new ToolStripMenuItem("Forget phones", null, (_, _) => _companion.ForgetAll("forgotten from the tray"));
        UpdatePhone();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _status,
            new ToolStripSeparator(),
            _terminate,
            _forget,
            _forgetPhone,
            _showFrame,
            new ToolStripMenuItem("Recent alerts…", null, (_, _) => ShowAlerts()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => _requestShutdown()),
        ]);

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "Glasscast – idle",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.BalloonTipClicked += (_, _) => ShowAlerts();

        _hotkey = new TerminateHotkey(() => _coordinator.TerminateActiveSession());
        if (!_hotkey.Registered)
        {
            _tray.ShowBalloonTip(5000, "Glasscast", $"{HotkeyText} is taken by another app; use the tray menu to end sessions.", ToolTipIcon.Warning);
        }

        _flushTimer.Tick += (_, _) => ShowNotice(_throttle.Flush());
        _flushTimer.Start();

        _coordinator.RequestOpened += OnRequestOpened;
        _coordinator.RequestClosed += OnRequestClosed;
        _coordinator.SessionChanged += OnSessionChanged;
        _coordinator.DeviceGrantChanged += OnDeviceGrantChanged;
        _castArea.Changed += OnCastAreaChanged;
        _castArea.AudioChanged += OnAudioChanged;
        _alerts.Raised += OnAlert;
        _companion.RequestOpened += OnPhoneRequestOpened;
        _companion.RequestClosed += OnPhoneRequestClosed;
        _companion.Changed += OnPhoneChanged;

        // A pairing request may already be pending if the host started first.
        if (_coordinator.PendingRequest is { } pending)
        {
            OnRequestOpened(pending);
        }
        if (_companion.PendingRequest is { } phonePending)
        {
            OnPhoneRequestOpened(phonePending);
        }
    }

    /// <summary>Thread-safe: ends the UI loop (used when the host shuts down).</summary>
    public void RequestExit() => Ui(ExitThread);

    private void OnRequestOpened(PairingRequest request) => Ui(() =>
    {
        _popup?.Dismiss();
        _popup = new ApprovePopup(request, _coordinator);
        _popup.FormClosed += (_, _) => _popup = null;
        _popup.Show();
        _tray.Icon = _pendingIcon;
        _tray.Text = $"Glasscast – pairing request {request.Code}";
        _status.Text = $"Pairing request {request.Code} from {request.RemoteAddress}";
    });

    private void OnRequestClosed(PairingRequest request) => Ui(() =>
    {
        if (_popup?.RequestId == request.Id)
        {
            _popup.Dismiss();
        }
        if (_coordinator.ActiveSession is null)
        {
            SetIdle();
        }
    });

    private void OnDeviceGrantChanged(DateTimeOffset? expiresAt) => Ui(() => UpdateForget(expiresAt));

    /// <summary>The phone companion app asks to pair: same popup, its own code.</summary>
    private void OnPhoneRequestOpened(CompanionPairRequest pending) => Ui(() =>
    {
        _phonePopup?.Dismiss();
        _phonePopup = new ApprovePopup(pending.Request, "Phone pairing request",
            $"Approve only if this code matches the one on your phone ({pending.Name}):",
            "Approving lets the glasses control this phone through its companion app. The phone still asks "
            + "before every session. Forget phones in the tray menu undoes it.",
            _companion.Approve, _companion.Reject);
        _phonePopup.FormClosed += (_, _) => _phonePopup = null;
        _phonePopup.Show();
    });

    private void OnPhoneRequestClosed(CompanionPairRequest pending) => Ui(() =>
    {
        if (_phonePopup?.RequestId == pending.Request.Id)
        {
            _phonePopup.Dismiss();
        }
    });

    private void OnPhoneChanged() => Ui(UpdatePhone);

    private void UpdatePhone()
    {
        var phones = _companion.Phones;
        _forgetPhone.Enabled = phones.Count > 0;
        _forgetPhone.Text = phones.Count switch
        {
            0 => "No phone paired",
            1 => $"Forget phone ({phones[0].Name}, {(phones[0].Connected ? "connected" : "offline")})",
            _ => $"Forget {phones.Count} phones ({phones.Count(p => p.Connected)} connected)",
        };
    }

    /// <summary>Remembered glasses reconnect without the popup until then; this undoes that.</summary>
    private void UpdateForget(DateTimeOffset? expiresAt)
    {
        _forget.Enabled = expiresAt is not null;
        _forget.Text = expiresAt is { } until
            ? $"Forget remembered glasses (until {until.ToLocalTime():ddd HH:mm})"
            : "No glasses remembered";
    }

    private void OnSessionChanged(ActiveSessionInfo? session) => Ui(() =>
    {
        if (session is null)
        {
            _banner.Hide();
            SetIdle();
            return;
        }

        _tray.Icon = _activeIcon;
        _tray.Text = Truncate($"Glasscast – session from {session.RemoteAddress}");
        _status.Text = $"Session active from {session.RemoteAddress} since {session.StartedAt.ToLocalTime():HH:mm}";
        _terminate.Enabled = true;
        _banner.ShowFor(session.RemoteAddress);
    });

    private void OnCastAreaChanged(CaptureRegion? region) => Ui(() => UpdateFrame(region));

    private void OnAudioChanged(bool on) => Ui(() => _banner.SetAudio(on));

    private void UpdateFrame(CaptureRegion? region)
    {
        if (region is null || !_showFrame.Checked)
        {
            _frame.Hide();
        }
        else
        {
            _frame.ShowAround(region);
        }
    }

    private void OnAlert(Alert alert) => Ui(() =>
    {
        ShowNotice(_throttle.Record(alert));
        _alertsForm?.Reload();
    });

    private void ShowNotice(AlertNotice? notice)
    {
        if (notice is not null)
        {
            _tray.ShowBalloonTip(8000, notice.Title, notice.Text, ToolTipIcon.Warning);
        }
    }

    private void SetIdle()
    {
        _tray.Icon = _idleIcon;
        _tray.Text = "Glasscast – idle";
        _status.Text = "Idle – waiting for glasses";
        _terminate.Enabled = false;
    }

    private void ShowAlerts()
    {
        if (_alertsForm is { IsDisposed: false })
        {
            _alertsForm.Reload();
            _alertsForm.Activate();
            return;
        }

        _alertsForm = new AlertsForm(_alerts);
        _alertsForm.FormClosed += (_, _) => _alertsForm = null;
        _alertsForm.Show();
    }

    private void Ui(Action action)
    {
        if (_marshal.IsDisposed)
        {
            return;
        }

        if (_marshal.InvokeRequired)
        {
            _marshal.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    // NotifyIcon tooltips are limited to 127 characters.
    private static string Truncate(string text) => text.Length <= 127 ? text : text[..127];

    protected override void ExitThreadCore()
    {
        _coordinator.RequestOpened -= OnRequestOpened;
        _coordinator.RequestClosed -= OnRequestClosed;
        _coordinator.SessionChanged -= OnSessionChanged;
        _coordinator.DeviceGrantChanged -= OnDeviceGrantChanged;
        _castArea.Changed -= OnCastAreaChanged;
        _castArea.AudioChanged -= OnAudioChanged;
        _alerts.Raised -= OnAlert;
        _companion.RequestOpened -= OnPhoneRequestOpened;
        _companion.RequestClosed -= OnPhoneRequestClosed;
        _companion.Changed -= OnPhoneChanged;
        _flushTimer.Stop();
        _hotkey.Dispose();
        _popup?.Dismiss();
        _phonePopup?.Dismiss();
        _alertsForm?.Close();
        _banner.Close();
        _frame.Close();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }
}

/// <summary>Global hotkey (Ctrl+Alt+Shift+X) that ends the active session.</summary>
internal sealed class TerminateHotkey : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x4752; // "GR"
    private const uint VK_X = 0x58;

    private readonly Action _onPressed;

    public TerminateHotkey(Action onPressed)
    {
        _onPressed = onPressed;
        CreateHandle(new CreateParams());
        Registered = NativeMethods.RegisterHotKey(Handle, HotkeyId,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, VK_X);
    }

    public bool Registered { get; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam == HotkeyId)
        {
            _onPressed();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        }
        DestroyHandle();
    }
}

internal static class TrayIcons
{
    public static Icon Dot(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            using var ring = new Pen(Color.FromArgb(230, 20, 24, 30), 2.5f);
            g.FillEllipse(fill, 3, 3, 26, 26);
            g.DrawEllipse(ring, 3, 3, 26, 26);
        }
        // Clone so the icon owns its data and the GDI handle can be released right away.
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
