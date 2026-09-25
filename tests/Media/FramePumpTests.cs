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

    private void Start(int minGapMs)
    {
        var pump = new FramePump(new FakeCapture(), Options.Create(new MediaOptions
        {
            FramesPerSecond = 50,
            KeyframeIntervalSeconds = 60, // no periodic keyframes during the test
            RequestedKeyframeMinGapMs = minGapMs,
        }), TimeProvider.System, NullLogger<FramePump>.Instance);
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
}
