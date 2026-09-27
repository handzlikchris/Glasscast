using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GlassesRemote.Server.Media;

/// <summary>
/// DIAGNOSTIC (Media:LinkTestOnStart): the first seconds of a session send a test pattern at
/// rising bitrates instead of the desktop. The pattern is noise, which the CBR encoder can't
/// compress, over a share of the frame that grows with the step (full-frame noise costs
/// ~1.6 Mbit/s even at the encoder's lowest quality), so each step really sends about its
/// bitrate; the glasses' figures in the stats log
/// (kbps received, lost, arrival delay) then show the rate at which delivery stops keeping up.
/// Run it once on the glasses and once in the phone's browser on the same network to tell the
/// phone-to-glasses hop from the internet path.
/// </summary>
public sealed class LinkTest(IReadOnlyList<int> stepsKbps, TimeSpan stepLength)
{
    private readonly Random _random = new();

    public TimeSpan Length => stepLength * stepsKbps.Count;

    /// <summary>The step's bitrate at this point of the session, or null once the test is over.</summary>
    public int? KbpsAt(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero || stepLength <= TimeSpan.Zero)
        {
            return null;
        }
        var step = (int)(elapsed / stepLength);
        return step < stepsKbps.Count ? stepsKbps[step] : null;
    }

    /// <summary>Fresh noise over the top <c>kbps / max step</c> of the frame (grey below), with the step written across the middle.</summary>
    public void Fill(byte[] bgra, int width, int height, int kbps)
    {
        var share = Math.Clamp((double)kbps / stepsKbps.Max(), 0.02, 1);
        var noisy = (int)(width * 4 * Math.Round(height * share));
        _random.NextBytes(bgra.AsSpan(0, noisy));
        bgra.AsSpan(noisy).Fill(0x80);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            using var graphics = Graphics.FromImage(bitmap);
            using var font = new Font(FontFamily.GenericSansSerif, 36, FontStyle.Bold, GraphicsUnit.Pixel);
            var band = new Rectangle(0, height / 2 - 40, width, 80);
            graphics.FillRectangle(Brushes.Black, band);
            using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString($"LINK TEST {kbps} kbps", font, Brushes.White, band, centred);
        }
        finally
        {
            handle.Free();
        }
    }
}
