using System.Runtime.InteropServices;
using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Ui;

/// <summary>
/// A thin frame drawn on the PC's monitor around the area the glasses are casting, so you
/// can fit a window into it. Drawn just outside the area, click-through, never activated,
/// and excluded from screen capture, so it never shows up on the glasses.
/// </summary>
internal sealed partial class CastFrame : Form
{
    private const int Thickness = 3;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private static readonly Color See = Color.Magenta;
    private static readonly Color Line = Color.FromArgb(255, 120, 30);

    public CastFrame()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = See;
        TransparencyKey = See;
        DoubleBuffered = true;
    }

    /// <summary>Shows the frame around a region of the primary monitor (physical pixels).</summary>
    public void ShowAround(CaptureRegion region)
    {
        var origin = System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Location;
        Bounds = new Rectangle(
            origin.X + region.X - Thickness,
            origin.Y + region.Y - Thickness,
            region.Width + 2 * Thickness,
            region.Height + 2 * Thickness);
        if (!Visible)
        {
            Show();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Line);
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        e.Graphics.FillRectangle(brush, 0, 0, w, Thickness);
        e.Graphics.FillRectangle(brush, 0, h - Thickness, w, Thickness);
        e.Graphics.FillRectangle(brush, 0, 0, Thickness, h);
        e.Graphics.FillRectangle(brush, w - Thickness, 0, Thickness, h);
    }

    // Don't steal focus from whatever the glasses are controlling.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 10 2004+. The frame sits outside the cast area anyway, so older builds are fine too.
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);
}
