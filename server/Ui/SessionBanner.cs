using System.Runtime.InteropServices;

namespace GlassesRemote.Server.Ui;

/// <summary>
/// Small always-on-top bar shown on the PC while the glasses are in control.
/// Excluded from screen capture, so it never appears in the glasses' view.
/// </summary>
internal sealed partial class SessionBanner : Form
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private readonly Label _text;

    public SessionBanner(string hotkeyText)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 110, 60);
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(460, 34);

        _text = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_text);

        HotkeyText = hotkeyText;
    }

    public string HotkeyText { get; }

    public void ShowFor(string remoteAddress)
    {
        _text.Text = $"● Glasses in control from {remoteAddress} – {HotkeyText} to end";
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 6);
        Show();
    }

    // Don't steal focus from whatever the glasses are controlling.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 10 2004+; older builds ignore it and the banner simply shows in the stream.
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
