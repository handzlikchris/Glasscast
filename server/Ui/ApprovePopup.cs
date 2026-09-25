using GlassesRemote.Server.Pairing;

namespace GlassesRemote.Server.Ui;

/// <summary>
/// Pops up when glasses ask to pair. You compare the code with the one shown on
/// the glasses and click Approve. Reject is the focused button and the Cancel
/// button, so Enter, Space or Esc pressed while typing elsewhere can only reject.
/// Closing the window rejects too.
/// </summary>
internal sealed class ApprovePopup : Form
{
    private readonly PairingRequest _request;
    private readonly PairingCoordinator _coordinator;
    private readonly Label _countdown;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _decided;

    public ApprovePopup(PairingRequest request, PairingCoordinator coordinator)
    {
        _request = request;
        _coordinator = coordinator;

        Text = "Glasses pairing request";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        var remembers = coordinator.DeviceGrantLifetime > TimeSpan.Zero;
        ClientSize = new Size(420, remembers ? 300 : 260);
        Font = new Font("Segoe UI", 10f);

        var intro = new Label
        {
            Text = "Approve only if this code matches the one on your glasses:",
            AutoSize = false,
            Location = new Point(20, 16),
            Size = new Size(380, 24),
        };

        var code = new Label
        {
            Text = request.Code,
            Font = new Font("Consolas", 32f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 44),
            Size = new Size(380, 64),
        };

        var origin = new Label
        {
            Text = $"From {request.RemoteAddress} at {request.CreatedAt.ToLocalTime():HH:mm:ss}",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 112),
            Size = new Size(380, 22),
        };

        _countdown = new Label
        {
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 136),
            Size = new Size(380, 22),
        };

        var reject = new Button
        {
            Text = "&Reject",
            Location = new Point(40, 184),
            Size = new Size(160, 44),
        };
        reject.Click += (_, _) => Decide(approve: false);

        var approve = new Button
        {
            Text = "&Approve",
            Location = new Point(220, 184),
            Size = new Size(160, 44),
            BackColor = Color.FromArgb(32, 110, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        approve.Click += (_, _) => Decide(approve: true);

        Controls.AddRange([intro, code, origin, _countdown, reject, approve]);
        if (remembers)
        {
            Controls.Add(new Label
            {
                Text = $"Approving also lets these glasses reconnect without asking for {coordinator.DeviceGrantLifetime.TotalHours:0} h. "
                       + "End session or Forget in the tray menu undoes that.",
                ForeColor = SystemColors.GrayText,
                Location = new Point(20, 240),
                Size = new Size(380, 44),
            });
        }

        // Never a default "accept" action: Enter must not approve.
        AcceptButton = null;
        CancelButton = reject;
        ActiveControl = reject;

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        UpdateCountdown();
    }

    public string RequestId => _request.Id;

    /// <summary>Closes without a decision (the request already ended elsewhere).</summary>
    public void Dismiss()
    {
        _decided = true;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        System.Media.SystemSounds.Asterisk.Play();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_decided)
        {
            _decided = true;
            _coordinator.Reject(_request.Id);
        }
        _timer.Stop();
        base.OnFormClosing(e);
    }

    private void Decide(bool approve)
    {
        if (_decided)
        {
            return;
        }

        _decided = true;
        if (approve)
        {
            _coordinator.Approve(_request.Id);
        }
        else
        {
            _coordinator.Reject(_request.Id);
        }
        Close();
    }

    private void UpdateCountdown()
    {
        var left = _request.ExpiresAt - DateTimeOffset.UtcNow;
        _countdown.Text = left > TimeSpan.Zero
            ? $"Rejects itself in {Math.Ceiling(left.TotalSeconds):0} s"
            : "Expired";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
