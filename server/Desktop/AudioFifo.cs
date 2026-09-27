namespace GlassesRemote.Server.Desktop;

/// <summary>
/// First in, first out buffer of interleaved stereo samples between the capture and the
/// encoder. Counts in frames (one sample per channel). One thread only.
/// </summary>
public sealed class AudioFifo
{
    private const int Channels = AudioFormat48k.Channels;

    private float[] _samples;
    private int _start;
    private int _count; // samples, not frames

    public AudioFifo(int capacityFrames = AudioFormat48k.SampleRate / 2)
    {
        _samples = new float[capacityFrames * Channels];
    }

    public int Frames => _count / Channels;

    /// <summary>Appends interleaved stereo samples (a whole number of frames).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        var length = interleaved.Length - interleaved.Length % Channels;
        if (length == 0)
        {
            return;
        }
        Reserve(length);
        for (var i = 0; i < length; i++)
        {
            _samples[(_start + _count + i) % _samples.Length] = interleaved[i];
        }
        _count += length;
    }

    /// <summary>Appends <paramref name="frames"/> frames of silence.</summary>
    public void WriteSilence(int frames)
    {
        var length = frames * Channels;
        Reserve(length);
        for (var i = 0; i < length; i++)
        {
            _samples[(_start + _count + i) % _samples.Length] = 0;
        }
        _count += length;
    }

    /// <summary>Moves exactly <c>destination.Length / 2</c> frames out; the caller checks <see cref="Frames"/> first.</summary>
    public void Read(Span<float> destination)
    {
        if (destination.Length > _count)
        {
            throw new InvalidOperationException("Not enough audio buffered");
        }
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = _samples[(_start + i) % _samples.Length];
        }
        Drop(destination.Length / Channels);
    }

    /// <summary>Throws away the oldest <paramref name="frames"/> frames (or everything).</summary>
    public void Drop(int frames)
    {
        var length = Math.Min(_count, frames * Channels);
        _start = (_start + length) % _samples.Length;
        _count -= length;
    }

    public void Clear() => Drop(Frames);

    private void Reserve(int more)
    {
        if (_count + more <= _samples.Length)
        {
            return;
        }
        var grown = new float[Math.Max(_samples.Length * 2, _count + more)];
        for (var i = 0; i < _count; i++)
        {
            grown[i] = _samples[(_start + i) % _samples.Length];
        }
        _samples = grown;
        _start = 0;
    }
}
