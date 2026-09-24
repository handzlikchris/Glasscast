using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Windows;

/// <summary>
/// Simplest reliable capture: GDI CopyFromScreen of just the requested rectangle,
/// then a bilinear scale into the letterboxed output frame. Good enough for a POC
/// at 15–30 fps; Windows.Graphics.Capture can replace it behind ICaptureSource.
/// Not thread-safe: one frame pump uses it at a time.
/// </summary>
public sealed class GdiCaptureSource : ICaptureSource
{
    private readonly ILogger<GdiCaptureSource> _logger;
    private Bitmap? _grab;
    private Bitmap? _frame;
    private Graphics? _frameGraphics;
    private DateTime _lastErrorLog = DateTime.MinValue;

    public GdiCaptureSource(ILogger<GdiCaptureSource> logger)
    {
        _logger = logger;
    }

    public bool TryCapture(PixelRect source, PixelSize frame, byte[] bgra)
    {
        if (source.Width <= 0 || source.Height <= 0 || bgra.Length < frame.Width * frame.Height * 4)
        {
            return false;
        }

        try
        {
            EnsureBuffers(source, frame);

            using (var g = Graphics.FromImage(_grab!))
            {
                g.CopyFromScreen(source.X, source.Y, 0, 0, new Size(source.Width, source.Height), CopyPixelOperation.SourceCopy);
            }

            var fit = RegionMath.Fit(new PixelSize(source.Width, source.Height), frame);
            _frameGraphics!.Clear(Color.Black);
            _frameGraphics.DrawImage(_grab!, new Rectangle(fit.X, fit.Y, fit.Width, fit.Height));

            var data = _frame!.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(data.Scan0, bgra, 0, frame.Width * frame.Height * 4);
            }
            finally
            {
                _frame.UnlockBits(data);
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException)
        {
            // Happens while the secure desktop (UAC, lock screen) is showing.
            if (DateTime.UtcNow - _lastErrorLog > TimeSpan.FromSeconds(10))
            {
                _logger.LogWarning("Screen capture failed: {Message}", ex.Message);
                _lastErrorLog = DateTime.UtcNow;
            }
            return false;
        }
    }

    private void EnsureBuffers(PixelRect source, PixelSize frame)
    {
        if (_grab is null || _grab.Width != source.Width || _grab.Height != source.Height)
        {
            _grab?.Dispose();
            _grab = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        }

        if (_frame is null || _frame.Width != frame.Width || _frame.Height != frame.Height)
        {
            _frameGraphics?.Dispose();
            _frame?.Dispose();
            _frame = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
            _frameGraphics = Graphics.FromImage(_frame);
            _frameGraphics.InterpolationMode = InterpolationMode.Bilinear;
            _frameGraphics.PixelOffsetMode = PixelOffsetMode.Half;
            _frameGraphics.CompositingMode = CompositingMode.SourceCopy;
        }
    }

    public void Dispose()
    {
        _frameGraphics?.Dispose();
        _frame?.Dispose();
        _grab?.Dispose();
    }
}
