using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

public sealed class AudioTimelineTests
{
    private const int Frame = 960; // 20 ms
    private const int Prebuffer = 480;
    private const int Max = 3840; // 80 ms

    private readonly AudioTimeline _timeline = new(Frame, Prebuffer, Max);
    private readonly AudioFifo _fifo = new();
    private readonly float[] _frame = new float[Frame * 2];

    private void Capture(int frames, float value = 0.25f)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, value);
        _fifo.Write(samples);
    }

    [Fact]
    public void A_frame_is_due_once_its_time_has_passed()
    {
        _timeline.Restart(1000);

        Assert.False(_timeline.TryNextFrame(1000 + Frame - 1, _fifo, _frame, out _, out _));
        Assert.True(_timeline.TryNextFrame(1000 + Frame, _fifo, _frame, out var position, out _));
        Assert.Equal(1000, position);
        Assert.False(_timeline.TryNextFrame(1000 + Frame, _fifo, _frame, out _, out _));
    }

    [Fact]
    public void Nothing_captured_goes_out_as_silence_on_time()
    {
        _timeline.Restart(0);
        Array.Fill(_frame, 1f);

        var positions = new List<long>();
        while (_timeline.TryNextFrame(3 * Frame, _fifo, _frame, out var position, out var captured))
        {
            Assert.False(captured);
            positions.Add(position);
        }

        Assert.Equal([0L, Frame, 2 * Frame], positions);
        Assert.All(_frame, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Captured_sound_is_used_once_a_frame_plus_the_prebuffer_is_waiting()
    {
        _timeline.Restart(0);
        Capture(Frame); // a frame, but no margin yet

        Assert.True(_timeline.TryNextFrame(Frame, _fifo, _frame, out _, out var captured));
        Assert.False(captured);

        Capture(Prebuffer);
        Assert.True(_timeline.TryNextFrame(2 * Frame, _fifo, _frame, out _, out captured));
        Assert.True(captured);
        Assert.Equal(0.25f, _frame[0]);
        Assert.Equal(Prebuffer, _fifo.Frames); // the margin stays
    }

    [Fact]
    public void Steady_10ms_capture_plays_without_gaps()
    {
        _timeline.Restart(0);
        var silent = 0;
        for (var now = 480L; now <= 48_000; now += 480) // WASAPI hands over 10 ms at a time
        {
            Capture(480);
            while (_timeline.TryNextFrame(now, _fifo, _frame, out _, out var captured))
            {
                silent += captured ? 0 : 1;
            }
        }

        Assert.InRange(silent, 1, 2); // only while the first frame plus margin builds up
        Assert.InRange(_fifo.Frames, 0, Frame + Prebuffer);
    }

    [Fact]
    public void A_backlog_is_trimmed_so_the_delay_never_grows()
    {
        _timeline.Restart(0);
        Capture(10 * Frame);

        Assert.True(_timeline.TryNextFrame(Frame, _fifo, _frame, out _, out var captured));

        Assert.True(captured);
        Assert.Equal(Prebuffer, _fifo.Frames); // trimmed to a frame + prebuffer, one frame sent
    }

    [Fact]
    public void After_a_long_stall_it_skips_ahead_instead_of_catching_up()
    {
        _timeline.Restart(0);
        Capture(Frame * 2);

        var now = 48_000L; // a second later
        var count = 0;
        long first = -1;
        while (_timeline.TryNextFrame(now, _fifo, _frame, out var position, out _))
        {
            first = first < 0 ? position : first;
            count++;
        }

        Assert.Equal(1, count);
        Assert.Equal(now - Frame, first);
    }
}
