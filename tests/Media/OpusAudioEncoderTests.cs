using System.Diagnostics;
using Concentus;
using GlassesRemote.Server.Media;
using Xunit.Abstractions;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>The real Opus encoder (Concentus), decoded back like the glasses would.</summary>
public sealed class OpusAudioEncoderTests(ITestOutputHelper output)
{
    private static float[] Tone(int frame, int index, float amplitude = 0.3f)
    {
        var samples = new float[frame * 2];
        for (var i = 0; i < frame; i++)
        {
            var t = (index * frame + i) / 48_000f;
            samples[2 * i] = amplitude * MathF.Sin(2 * MathF.PI * 440 * t);
            samples[2 * i + 1] = amplitude * MathF.Sin(2 * MathF.PI * 660 * t);
        }
        return samples;
    }

    [Fact]
    public void A_tone_comes_back_out_of_a_decoder_in_stereo_near_the_bitrate()
    {
        var encoder = new OpusAudioEncoder(kbps: 40, frameMs: 20);
        var decoder = OpusCodecFactory.CreateDecoder(48_000, 2);
        var decoded = new float[encoder.FrameSamples * 2];
        var bytes = 0;
        double left = 0, right = 0;
        for (var i = 0; i < 50; i++) // one second
        {
            var packet = encoder.Encode(Tone(encoder.FrameSamples, i)).ToArray();
            Assert.False(OpusAudioEncoder.IsDtx(packet));
            bytes += packet.Length;
            Assert.Equal(encoder.FrameSamples, decoder.Decode(packet, decoded, encoder.FrameSamples));
            if (i >= 10)
            {
                for (var s = 0; s < encoder.FrameSamples; s++)
                {
                    left += decoded[2 * s] * decoded[2 * s];
                    right += decoded[2 * s + 1] * decoded[2 * s + 1];
                }
            }
        }

        Assert.Equal(960, encoder.FrameSamples);
        Assert.InRange(bytes * 8 / 1000.0, 25, 55); // kbit in one second: VBR around 40
        // A 0.3 sine has an RMS of 0.21, on both sides.
        Assert.InRange(Math.Sqrt(left / (40 * 960)), 0.15, 0.3);
        Assert.InRange(Math.Sqrt(right / (40 * 960)), 0.15, 0.3);
    }

    [Fact]
    public void Digital_silence_is_recognised_and_encodes_to_almost_nothing()
    {
        var encoder = new OpusAudioEncoder(40, 20);
        var silence = new float[encoder.FrameSamples * 2];
        Assert.True(OpusAudioEncoder.IsDigitalSilence(silence));
        Assert.False(OpusAudioEncoder.IsDigitalSilence(Tone(encoder.FrameSamples, 0, amplitude: 0.001f)));

        // General audio (CELT) has no DTX of its own: silence still makes a few bytes a frame,
        // which the pump holds back (see AudioPumpTests).
        for (var i = 0; i < 10; i++)
        {
            Assert.InRange(encoder.Encode(silence).Length, 1, 8);
        }
    }

    [Fact]
    public void Encoding_costs_a_small_part_of_real_time()
    {
        var encoder = new OpusAudioEncoder(40, 20);
        var frames = Enumerable.Range(0, 250).Select(i => Tone(encoder.FrameSamples, i)).ToArray(); // 5 s
        encoder.Encode(frames[0]); // warm up
        var watch = Stopwatch.StartNew();
        foreach (var frame in frames)
        {
            encoder.Encode(frame);
        }
        var share = watch.Elapsed.TotalSeconds / 5;
        output.WriteLine($"Opus encode: {share:P1} of one core");

        Assert.True(share < 0.2, $"{share:P1} of one core");
    }
}
