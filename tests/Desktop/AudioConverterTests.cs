using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Tests.Desktop;

public sealed class AudioConverterTests
{
    [Fact]
    public void Fifo_keeps_order_across_the_wrap_and_grows_when_full()
    {
        var fifo = new AudioFifo(capacityFrames: 4);
        fifo.Write([1, 1, 2, 2, 3, 3]);
        var two = new float[4];
        fifo.Read(two);
        Assert.Equal([1f, 1, 2, 2], two);

        fifo.Write([4, 4, 5, 5, 6, 6, 7, 7]); // wraps, then needs more room
        fifo.WriteSilence(1);
        Assert.Equal(6, fifo.Frames);
        var all = new float[12];
        fifo.Read(all);
        Assert.Equal([3f, 3, 4, 4, 5, 5, 6, 6, 7, 7, 0, 0], all);
        Assert.Throws<InvalidOperationException>(() => fifo.Read(new float[2]));
    }

    [Fact]
    public void Resampling_44k1_mono_gives_the_48k_stereo_frame_count_across_packets()
    {
        var converter = new AudioConverter(44_100, 1);
        var fifo = new AudioFifo();
        var packet = new float[441]; // 10 ms at a time, as WASAPI delivers
        Array.Fill(packet, 0.5f);
        for (var i = 0; i < 100; i++)
        {
            converter.Convert(packet, fifo);
        }

        // One second in: 48,000 frames out, give or take the one held back for interpolation.
        Assert.InRange(fifo.Frames, 47_998, 48_000);
        var frame = new float[2];
        fifo.Read(frame);
        Assert.Equal([0.5f, 0.5f], frame); // mono to both sides
    }

    [Fact]
    public void Interpolates_between_samples_and_across_packet_boundaries()
    {
        var converter = new AudioConverter(24_000, 2); // two output frames per input frame
        var fifo = new AudioFifo();
        converter.Convert([0, 0, 1, -1], fifo);
        converter.Convert([2, -2], fifo);

        var converted = new float[fifo.Frames * 2];
        fifo.Read(converted);
        Assert.Equal([0f, 0, 0.5f, -0.5f, 1, -1, 1.5f, -1.5f], converted);
    }

    [Fact]
    public void Surround_keeps_the_centre_channel_in_both_sides()
    {
        var converter = new AudioConverter(48_000, 6); // FL FR FC LFE BL BR
        var fifo = new AudioFifo();
        converter.Convert([0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1], fifo);

        var frame = new float[2];
        fifo.Read(frame);
        Assert.Equal(0.707f / 1.707f, frame[0], 3);
        Assert.Equal(frame[0], frame[1]);
    }
}
