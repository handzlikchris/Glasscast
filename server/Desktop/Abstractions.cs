using GlassesRemote.Server.Protocol;

namespace GlassesRemote.Server.Desktop;

/// <summary>The primary monitor, in physical pixels (the process is per-monitor DPI aware).</summary>
public interface IScreen
{
    PixelSize PrimarySize { get; }
}

/// <summary>
/// Grabs part of the primary monitor and scales it into a fixed-size BGRA frame,
/// letterboxed with black. Kept behind an interface so GDI capture can later be
/// swapped for Windows.Graphics.Capture.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Fills <paramref name="bgra"/> (frame.Width × frame.Height × 4 bytes). False if capture failed.</summary>
    bool TryCapture(PixelRect source, PixelSize frame, byte[] bgra);
}

/// <summary>Mouse and keyboard injection into the interactive desktop.</summary>
public interface IInputInjector
{
    /// <summary>Moves the cursor to a primary-monitor pixel.</summary>
    void MoveTo(int x, int y);

    void Click(MouseButton button);

    /// <summary>Presses (down) or releases a button without the other half: a held drag.</summary>
    void Button(MouseButton button, bool down);

    /// <summary>Wheel movement in Windows units (120 per notch); positive scrolls up.</summary>
    void Wheel(int delta);

    /// <summary>Types literal Unicode text. Never interprets it.</summary>
    void TypeText(string text);

    void Press(KeyCommand key);
}

/// <summary>Keeps the PC awake and the display on while a session runs.</summary>
public interface IKeepAwake
{
    IDisposable Acquire();
}

/// <summary>
/// The sound the PC is playing (every app, as mixed for the default output device), at
/// <see cref="AudioFormat48k.SampleRate"/> Hz, interleaved stereo floats.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>
    /// Appends what was played since the last call. While nothing plays Windows may deliver
    /// nothing at all. Throws when the device went away (unplugged, disabled).
    /// </summary>
    void ReadInto(AudioFifo fifo);

    /// <summary>The default output device is no longer the one being captured (headphones plugged in, ...).</summary>
    bool IsStale { get; }
}

public interface IAudioCaptureFactory
{
    /// <summary>Starts capturing the default output device; null when there is none.</summary>
    IAudioCapture? Start();
}

/// <summary>The one format audio moves in between capture and the Opus encoder.</summary>
public static class AudioFormat48k
{
    public const int SampleRate = 48_000;

    public const int Channels = 2;
}
