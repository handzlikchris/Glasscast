namespace GlassesRemote.Server.Desktop;

/// <summary>
/// Turns captured sound of another rate or channel count into 48 kHz stereo, for output devices
/// where Windows won't convert it for us. Linear interpolation (fine for a stream that ends in
/// the glasses' speakers); more than two channels are folded to stereo in the usual order
/// (front left, front right, centre, ...), keeping the centre (dialogue) and dropping the rest.
/// Keeps its position across calls, so a stream split into packets resamples seamlessly.
/// </summary>
public sealed class AudioConverter
{
    private const float CentreGain = 0.707f;

    private readonly int _channels;
    private readonly double _step;
    private double _position; // in input frames, relative to the current input; -1 = the previous input's last frame
    private float _lastLeft;
    private float _lastRight;
    private float[] _output = [];

    public AudioConverter(int sampleRate, int channels)
    {
        if (sampleRate <= 0 || channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Bad audio format");
        }
        _channels = channels;
        _step = sampleRate / (double)AudioFormat48k.SampleRate;
    }

    /// <summary>Converts interleaved <paramref name="input"/> (a whole number of frames) into <paramref name="fifo"/>.</summary>
    public void Convert(ReadOnlySpan<float> input, AudioFifo fifo)
    {
        var frames = input.Length / _channels;
        if (frames == 0)
        {
            return;
        }

        var most = (int)Math.Ceiling((frames + 1) / _step) + 1;
        if (_output.Length < most * 2)
        {
            _output = new float[most * 2];
        }

        var written = 0;
        while (_position < frames - 1)
        {
            var index = (int)Math.Floor(_position);
            var fraction = (float)(_position - index);
            var (l0, r0) = index < 0 ? (_lastLeft, _lastRight) : Stereo(input, index);
            var (l1, r1) = Stereo(input, index + 1);
            _output[written++] = l0 + (l1 - l0) * fraction;
            _output[written++] = r0 + (r1 - r0) * fraction;
            _position += _step;
        }

        _position -= frames;
        (_lastLeft, _lastRight) = Stereo(input, frames - 1);
        fifo.Write(_output.AsSpan(0, written));
    }

    private (float Left, float Right) Stereo(ReadOnlySpan<float> input, int frame)
    {
        var i = frame * _channels;
        return _channels switch
        {
            1 => (input[i], input[i]),
            2 => (input[i], input[i + 1]),
            _ => ((input[i] + CentreGain * input[i + 2]) / (1 + CentreGain), (input[i + 1] + CentreGain * input[i + 2]) / (1 + CentreGain)),
        };
    }
}
