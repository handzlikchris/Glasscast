using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>The glasses' keyframe requests (PLI/FIR) are answered at once, but not flooded.</summary>
public sealed class FramePumpTests : IAsyncDisposable
{
    private readonly FakePeer _peer = new();
    private readonly FakeEncoder _encoder = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<MediaStats> _stats = [];
    private Task _running = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _running.ContinueWith(_ => { });
    }

    private readonly FakeCapture _capture = new();

    private void Start(int minGapMs, Action<MediaOptions>? configure = null)
    {
        var options = new MediaOptions
        {
            FramesPerSecond = 50,
            KeyframeIntervalSeconds = 60, // no periodic keyframes during the test
            RequestedKeyframeMinGapMs = minGapMs,
        };
        configure?.Invoke(options);
        var pump = new FramePump(_capture, Options.Create(options), TimeProvider.System, NullLogger<FramePump>.Instance);
        _peer.ApplyAnswer("v=0");
        _running = pump.RunAsync(_peer, _encoder, () => new PixelRect(0, 0, 600, 600),
            s => { lock (_stats) _stats.Add(s); }, _stop.Token);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_keyframe_request_is_answered_on_the_next_frames()
    {
        Start(minGapMs: 0);
        await WaitUntil(() => _peer.FramesSent >= 5);
        var before = _encoder.KeyframesForced;

        _peer.RequestKeyframe();

        await WaitUntil(() => _encoder.KeyframesForced > before, timeoutMs: 500);
        await WaitUntil(() => _stats.Count > 0 && _stats.Sum(s => s.KeyframeRequests) == 1, timeoutMs: 2000);
    }

    [Fact]
    public async Task A_burst_of_requests_gets_one_keyframe_and_each_request_is_counted()
    {
        Start(minGapMs: 800);
        await WaitUntil(() => _peer.FramesSent >= 50); // past the gap after the first keyframe
        var before = _encoder.KeyframesForced;

        for (var i = 0; i < 5; i++)
        {
            _peer.RequestKeyframe();
            await Task.Delay(40);
        }
        await Task.Delay(300);

        Assert.Equal(before + 1, _encoder.KeyframesForced);
        await WaitUntil(() => _stats.Sum(s => s.KeyframeRequests) == 5);
    }

    [Fact]
    public async Task A_request_inside_the_gap_waits_for_it_rather_than_being_dropped()
    {
        Start(minGapMs: 400);
        await WaitUntil(() => _peer.FramesSent >= 2);
        var before = _encoder.KeyframesForced; // includes the stream's first keyframe, just sent

        _peer.RequestKeyframe();
        await Task.Delay(150);
        Assert.Equal(before, _encoder.KeyframesForced);

        await WaitUntil(() => _encoder.KeyframesForced == before + 1, timeoutMs: 1000);
    }

    [Fact]
    public async Task Loss_reported_by_the_glasses_lowers_the_encoder_target()
    {
        Start(minGapMs: 0, o => (o.TargetKbps, o.StartKbps) = (2500, 2000));
        await WaitUntil(() => _peer.FramesSent >= 5);

        _peer.ReportFeedback(new ReceiverFeedback(0.5, null)); // 50% lost: -25%

        await WaitUntil(() => _encoder.TargetKbps == 1500, timeoutMs: 1000);
        await WaitUntil(() => _stats.Any(s => s.TargetKbps == 1500 && s.LossPct == 50));
    }

    [Fact]
    public async Task The_link_test_steps_through_its_bitrates_on_a_test_pattern_then_shows_the_desktop()
    {
        Start(minGapMs: 0, o =>
        {
            o.LinkTestOnStart = true;
            o.LinkTestStepsKbps = [700, 900];
            o.LinkTestStepSeconds = 1;
        });

        await WaitUntil(() => _encoder.TargetKbps == 700, timeoutMs: 1000);
        _peer.ReportFeedback(new ReceiverFeedback(0.9, null)); // ignored during the test
        Assert.Empty(_capture.Sources);
        await WaitUntil(() => _encoder.TargetKbps == 900, timeoutMs: 2000);
        await WaitUntil(() => _encoder.TargetKbps == 1000, timeoutMs: 2000); // back to the start target
        await WaitUntil(() => _capture.Sources.Count > 0);
        Assert.Contains(_stats, s => s.LinkTestKbps > 0); // the step when each 1 s window closed
    }
}
