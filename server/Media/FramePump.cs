using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Media;

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
    private readonly ILogger<FramePump> _logger;

    public FramePump(ICaptureSource capture, IOptions<MediaOptions> options, ILogger<FramePump> logger)
    {
        _capture = capture;
        _options = options.Value;
        _logger = logger;
    }

    /// <param name="currentSource">Returns the monitor rectangle to show right now (overview or region).</param>
    /// <param name="onStats">Called about once a second with frames sent and mean capture+encode time.</param>
    public async Task RunAsync(IMediaPeer peer, IFrameEncoder encoder, Func<PixelRect> currentSource,
        Action<double, double>? onStats, CancellationToken ct)
    {
        await WaitForConnectionAsync(peer, ct);

        var fps = Math.Clamp(_options.FramesPerSecond, 1, 60);
        var frame = new PixelSize(_options.FrameWidth, _options.FrameHeight);
        var bgra = new byte[frame.Width * frame.Height * 4];
        var rtpDuration = (uint)(RtpClockRate / fps);
        var keyframeEvery = TimeSpan.FromSeconds(Math.Max(1, _options.KeyframeIntervalSeconds));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));
        var lastSource = default(PixelRect);
        var lastKeyframe = DateTime.UtcNow;
        var statsStart = DateTime.UtcNow;
        var framesSent = 0;
        var busyMs = 0.0;

        encoder.ForceKeyFrame();

        while (await timer.WaitForNextTickAsync(ct))
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var source = currentSource();

            // A new source (mode or region change) or the periodic timer both warrant a keyframe.
            if (source != lastSource || DateTime.UtcNow - lastKeyframe >= keyframeEvery)
            {
                encoder.ForceKeyFrame();
                lastSource = source;
                lastKeyframe = DateTime.UtcNow;
            }

            if (!_capture.TryCapture(source, frame, bgra))
            {
                continue;
            }

            var encoded = encoder.Encode(bgra, frame.Width, frame.Height);
            if (encoded is { Length: > 0 } && peer.IsConnected)
            {
                peer.SendFrame(encoded, rtpDuration);
                framesSent++;
            }

            busyMs += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var elapsed = DateTime.UtcNow - statsStart;
            if (elapsed.TotalSeconds >= 1)
            {
                onStats?.Invoke(framesSent / elapsed.TotalSeconds, framesSent > 0 ? busyMs / framesSent : 0);
                statsStart = DateTime.UtcNow;
                framesSent = 0;
                busyMs = 0;
            }
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
