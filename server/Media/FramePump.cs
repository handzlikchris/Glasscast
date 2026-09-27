using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Media;

/// <summary>One sent frame: its RTP timestamp, when its capture started (Unix ms, server clock) and its size.</summary>
public readonly record struct FrameTiming(uint Rtp, long CapturedAtUnixMs, int Bytes);

/// <summary>
/// What the pump sent over about a second. Capture and encode times are per frame, as is send:
/// the wait in the pacer until the frame's last packet went out. The frame list lets the
/// glasses work out capture-to-display latency for each frame they show.
/// </summary>
public sealed record MediaStats(
    double Fps,
    double CaptureMs,
    double CaptureMaxMs,
    double EncodeMs,
    double EncodeMaxMs,
    double SendMs,
    double SendMaxMs,
    double Kbps,
    int Keyframes,
    int KeyframeRequests,
    IReadOnlyList<FrameTiming> Frames);

/// <summary>
/// Capture → encode → send at a fixed frame rate once the peer is connected.
///
/// Latest-frame-wins: each tick captures the screen as it is now. If capture
/// and encode take longer than a tick, PeriodicTimer coalesces the missed ticks
/// instead of queueing them, so a slow machine or link lowers the frame rate
/// but never builds a backlog.
/// </summary>
public sealed class FramePump
{
    private const int RtpClockRate = 90_000;

    private readonly ICaptureSource _capture;
    private readonly MediaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FramePump> _logger;

    public FramePump(ICaptureSource capture, IOptions<MediaOptions> options, TimeProvider time, ILogger<FramePump> logger)
    {
        _capture = capture;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <param name="currentSource">Returns the monitor rectangle to show right now (overview or region).</param>
    /// <param name="onStats">Called about once a second with what was sent since the last call.</param>
    public async Task RunAsync(IMediaPeer peer, IFrameEncoder encoder, Func<PixelRect> currentSource,
        Action<MediaStats>? onStats, CancellationToken ct)
    {
        await WaitForConnectionAsync(peer, ct);

        var fps = Math.Clamp(_options.FramesPerSecond, 1, 60);
        var frame = new PixelSize(_options.FrameWidth, _options.FrameHeight);
        var bgra = new byte[frame.Width * frame.Height * 4];
        var rtpDuration = (uint)(RtpClockRate / fps);
        var keyframeEvery = TimeSpan.FromSeconds(Math.Max(1, _options.KeyframeIntervalSeconds));
        var requestedGap = TimeSpan.FromMilliseconds(Math.Max(0, _options.RequestedKeyframeMinGapMs));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
        var lastSource = default(PixelRect);
        var lastKeyframe = DateTime.UtcNow;
        var window = new StatsWindow(_time.GetTimestamp());
        var keyframeWanted = 0;
        var requests = 0;
        void OnKeyframeRequested()
        {
            Interlocked.Increment(ref requests);
            Volatile.Write(ref keyframeWanted, 1);
        }
        peer.KeyframeRequested += OnKeyframeRequested;
        try
        {
            encoder.ForceKeyFrame();

            while (await timer.WaitForNextTickAsync(ct))
            {
                var started = _time.GetTimestamp();
                var capturedAt = _time.GetUtcNow().ToUnixTimeMilliseconds();
                var source = currentSource();

                // A source of a different size (mode switch, resized region) changes the whole picture,
                // so start it with a keyframe. A region that merely moves (edge panning) is just motion,
                // which delta frames handle far more cheaply; requested and periodic keyframes cover loss.
                var resized = source.Width != lastSource.Width || source.Height != lastSource.Height;
                // The glasses asked for one: send it now (unless one went out moments ago; the request
                // stays pending until then).
                var requested = Volatile.Read(ref keyframeWanted) == 1 && DateTime.UtcNow - lastKeyframe >= requestedGap;
                if (resized || requested || DateTime.UtcNow - lastKeyframe >= keyframeEvery)
                {
                    Volatile.Write(ref keyframeWanted, 0);
                    encoder.ForceKeyFrame();
                    lastKeyframe = DateTime.UtcNow;
                    window.Keyframes++;
                }
                lastSource = source;

                if (_capture.TryCapture(source, frame, bgra))
                {
                    var captured = _time.GetTimestamp();
                    var encoded = encoder.Encode(bgra, frame.Width, frame.Height);
                    var encodedAt = _time.GetTimestamp();

                    if (encoded is { Length: > 0 } && peer.IsConnected)
                    {
                        var rtp = peer.SendFrame(encoded, rtpDuration);
                        window.Add(new FrameTiming(rtp, capturedAt, encoded.Length),
                            _time.GetElapsedTime(started, captured).TotalMilliseconds,
                            _time.GetElapsedTime(captured, encodedAt).TotalMilliseconds);
                    }
                }

                var now = _time.GetTimestamp();
                if (_time.GetElapsedTime(window.Started, now) >= TimeSpan.FromSeconds(1))
                {
                    window.KeyframeRequests = Interlocked.Exchange(ref requests, 0);
                    window.Send = peer.TakeSendDelay();
                    onStats?.Invoke(window.ToStats(_time.GetElapsedTime(window.Started, now)));
                    window = new StatsWindow(now);
                }
            }
        }
        finally
        {
            peer.KeyframeRequested -= OnKeyframeRequested;
        }
    }

    /// <summary>Frames sent during one stats interval.</summary>
    private sealed class StatsWindow(long started)
    {
        private readonly List<FrameTiming> _frames = [];
        private double _captureMs;
        private double _captureMaxMs;
        private double _encodeMs;
        private double _encodeMaxMs;

        public long Started { get; } = started;

        public int Keyframes { get; set; }

        public int KeyframeRequests { get; set; }

        public SendDelay Send { get; set; }

        public void Add(FrameTiming frame, double captureMs, double encodeMs)
        {
            _frames.Add(frame);
            _captureMs += captureMs;
            _captureMaxMs = Math.Max(_captureMaxMs, captureMs);
            _encodeMs += encodeMs;
            _encodeMaxMs = Math.Max(_encodeMaxMs, encodeMs);
        }

        public MediaStats ToStats(TimeSpan elapsed)
        {
            var count = Math.Max(1, _frames.Count);
            var bytes = _frames.Sum(f => (long)f.Bytes);
            return new MediaStats(
                Fps: _frames.Count / elapsed.TotalSeconds,
                CaptureMs: _captureMs / count,
                CaptureMaxMs: _captureMaxMs,
                EncodeMs: _encodeMs / count,
                EncodeMaxMs: _encodeMaxMs,
                SendMs: Send.AvgMs,
                SendMaxMs: Send.MaxMs,
                Kbps: bytes * 8 / elapsed.TotalSeconds / 1000,
                Keyframes: Keyframes,
                KeyframeRequests: KeyframeRequests,
                Frames: _frames);
        }
    }

    private async Task WaitForConnectionAsync(IMediaPeer peer, CancellationToken ct)
    {
        if (peer.IsConnected)
        {
            return;
        }

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnected() => connected.TrySetResult();
        peer.Connected += OnConnected;
        try
        {
            if (!peer.IsConnected)
            {
                await connected.Task.WaitAsync(ct);
            }
        }
        finally
        {
            peer.Connected -= OnConnected;
        }

        _logger.LogInformation("Media connected; streaming");
    }
}
