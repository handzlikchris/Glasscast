using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace WebRtcSpike;

/// <summary>
/// Renders a 600×600 synthetic frame: timestamp barcode, a moving bar (makes
/// stutter and dropped frames obvious), a grid for sharpness, and the server
/// time in large text. Output is BGRA, ready for the VP8 encoder.
/// </summary>
public sealed class TestPatternRenderer : IDisposable
{
    public const int Width = 600;
    public const int Height = 600;

    private readonly Bitmap _bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    private readonly Graphics _graphics;
    private readonly Font _timeFont = new("Consolas", 44, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _labelFont = new("Consolas", 22, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Pen _gridPen = new(Color.FromArgb(70, 90, 110), 1);
    private readonly Brush _background = new SolidBrush(Color.FromArgb(18, 22, 30));
    private readonly Brush _bar = new SolidBrush(Color.FromArgb(255, 170, 40));
    private readonly byte[] _buffer = new byte[Width * Height * 4];

    public TestPatternRenderer()
    {
        _graphics = Graphics.FromImage(_bitmap);
        _graphics.SmoothingMode = SmoothingMode.None;
        _graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    /// <summary>Renders one frame and returns the shared BGRA buffer (valid until the next call).</summary>
    public byte[] Render(long frameNumber, long serverUnixMs)
    {
        var g = _graphics;
        g.FillRectangle(_background, 0, 0, Width, Height);

        for (var x = 0; x <= Width; x += 50)
        {
            g.DrawLine(_gridPen, x, 40, x, Height);
        }
        for (var y = 50; y <= Height; y += 50)
        {
            g.DrawLine(_gridPen, 0, y, Width, y);
        }

        const int barWidth = 60;
        var travel = Width + barWidth;
        var barX = (int)(frameNumber * 10 % travel) - barWidth;
        g.FillRectangle(_bar, barX, 160, barWidth, 220);

        var time = DateTimeOffset.FromUnixTimeMilliseconds(serverUnixMs).ToLocalTime();
        g.DrawString(time.ToString("HH:mm:ss.fff"), _timeFont, Brushes.White, 30, 420);
        g.DrawString($"frame {frameNumber}", _labelFont, Brushes.Gainsboro, 32, 490);
        g.DrawString("WebRTC spike · test pattern", _labelFont, Brushes.Gainsboro, 32, 530);

        TimestampBarcode.Draw(g, serverUnixMs);

        var rect = new Rectangle(0, 0, Width, Height);
        var data = _bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // 32bpp rows are already 4-byte aligned, so stride == Width * 4.
            Marshal.Copy(data.Scan0, _buffer, 0, _buffer.Length);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        return _buffer;
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
        _timeFont.Dispose();
        _labelFont.Dispose();
        _gridPen.Dispose();
        _background.Dispose();
        _bar.Dispose();
    }
}
