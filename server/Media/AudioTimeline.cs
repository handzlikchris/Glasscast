using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Media;

/// <summary>
/// Keeps the PC's sound on wall-clock time. Frames go out when their time has passed, whether
/// or not anything was captured: Windows delivers nothing while nothing plays, and a frame of
/// silence (which Opus turns into DTX, i.e. nothing sent) keeps the RTP clock moving with real
/// time, as the glasses' jitter buffer expects.
///
/// Captured audio waits in a <see cref="AudioFifo"/>. It's used once a frame plus
/// <c>prebuffer</c> has built up (so a capture packet arriving a little late doesn't cut a hole
/// in the sound), and trimmed back when more than <c>maxBuffered</c> piles up (the capture clock
/// runs a touch faster than ours, or the pump stalled), so the delay never grows. A frame due
/// while too little is buffered goes out as silence, and buffering starts over.
///
/// Positions count samples per channel since the pump started, which makes them the RTP timestamp
/// (Opus uses a 48 kHz clock) once a random base is added.
/// </summary>
public sealed class AudioTimeline(int frameSamples, int prebufferSamples, int maxBufferedSamples)
{
    /// <summary>After a stall longer than this (sleep, debugger), skip ahead instead of catching up.</summary>
    private const int MaxFramesBehind = 10;

    private long _next;
    private bool _primed;

    /// <summary>Starts over at <paramref name="now"/> (audio just turned on): the first frame is due one frame later.</summary>
    public void Restart(long now)
    {
        _next = now;
        _primed = false;
    }

    /// <summary>
    /// If a frame is due by <paramref name="now"/>, fills <paramref name="frame"/>
    /// (frameSamples × 2 floats) with captured sound or silence and returns true.
    /// </summary>
    /// <param name="position">Where the frame starts on the timeline (the RTP timestamp, less its base).</param>
    /// <param name="captured">False when the frame is silence because too little was buffered.</param>
    public bool TryNextFrame(long now, AudioFifo fifo, Span<float> frame, out long position, out bool captured)
    {
        position = _next;
        captured = false;
        if (_next + frameSamples > now)
        {
            return false;
        }

        if (now - _next > (long)frameSamples * MaxFramesBehind)
        {
            // Too far behind to catch up usefully: drop what's buffered and resume from now.
            fifo.Clear();
            _primed = false;
            _next = now - frameSamples;
            position = _next;
        }

        if (fifo.Frames > maxBufferedSamples)
        {
            fifo.Drop(fifo.Frames - (frameSamples + prebufferSamples));
        }
        if (!_primed && fifo.Frames >= frameSamples + prebufferSamples)
        {
            _primed = true;
        }

        if (_primed && fifo.Frames >= frameSamples)
        {
            fifo.Read(frame);
            captured = true;
        }
        else
        {
            frame.Clear();
            _primed = false;
        }

        _next += frameSamples;
        return true;
    }
}
