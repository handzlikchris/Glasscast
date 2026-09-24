namespace GlassesRemote.Server.Desktop;

/// <summary>A rectangle on the primary monitor, in physical pixels.</summary>
public sealed record CaptureRegion(int X, int Y, int Width, int Height);

public readonly record struct PixelSize(int Width, int Height);

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class RegionMath
{
    /// <summary>Smallest region the glasses may pick; below this text is unreadable anyway.</summary>
    public const int MinRegionSize = 160;

    /// <summary>Keeps a requested region inside the monitor and at a sensible size.</summary>
    public static CaptureRegion Clamp(CaptureRegion requested, PixelSize monitor)
    {
        var width = Math.Clamp(requested.Width, Math.Min(MinRegionSize, monitor.Width), monitor.Width);
        var height = Math.Clamp(requested.Height, Math.Min(MinRegionSize, monitor.Height), monitor.Height);
        var x = Math.Clamp(requested.X, 0, monitor.Width - width);
        var y = Math.Clamp(requested.Y, 0, monitor.Height - height);
        return new CaptureRegion(x, y, width, height);
    }

    /// <summary>Starting region: a centred square, half the monitor's shorter side.</summary>
    public static CaptureRegion Default(PixelSize monitor)
    {
        var side = Math.Max(MinRegionSize, Math.Min(monitor.Width, monitor.Height) / 2);
        return Clamp(new CaptureRegion((monitor.Width - side) / 2, (monitor.Height - side) / 2, side, side), monitor);
    }

    /// <summary>Maps a 0..1 position inside the region to a monitor pixel. Always lands inside the region.</summary>
    public static (int X, int Y) ToScreen(CaptureRegion region, double nx, double ny)
    {
        nx = double.IsFinite(nx) ? Math.Clamp(nx, 0, 1) : 0.5;
        ny = double.IsFinite(ny) ? Math.Clamp(ny, 0, 1) : 0.5;
        var x = region.X + (int)Math.Round(nx * (region.Width - 1));
        var y = region.Y + (int)Math.Round(ny * (region.Height - 1));
        return (x, y);
    }

    /// <summary>
    /// Largest rectangle with the source's aspect ratio that fits in the frame,
    /// centred (letterboxed). The glasses client mirrors this to map touches.
    /// </summary>
    public static PixelRect Fit(PixelSize source, PixelSize frame)
    {
        var scale = Math.Min((double)frame.Width / source.Width, (double)frame.Height / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new PixelRect((frame.Width - width) / 2, (frame.Height - height) / 2, width, height);
    }
}
