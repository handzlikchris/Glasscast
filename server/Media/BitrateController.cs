namespace GlassesRemote.Server.Media;

/// <summary>What the glasses' RTCP said: fraction of packets lost since their last report, and
/// their bandwidth estimate (REMB). Either may be missing from a given report.</summary>
public readonly record struct ReceiverFeedback(double? LossFraction, int? RembKbps);

/// <summary>
/// Adapts the encoder's target bitrate to what the link to the glasses carries. At a fixed
/// 2.5 Mbit/s a link that carried ~0.8 Mbit/s queued up to 1.4 s of video, lost most packets
/// and set off a keyframe storm. Rules, after the loss-based half of Google Congestion Control:
/// - over 10% lost: cut by half the loss (20% lost → -10%);
/// - under 2% lost: raise by 8%, but only while the stream uses at least half its target
///   (a still screen sends a fraction of it and says nothing about the link);
/// REMB, the glasses' delay-based estimate, is only recorded (stats log), not acted on: Chrome
/// caps it near 1.5x what it receives and raises it by ~8% a second, so after a still screen it
/// lags far below what the link carries (in the harness: ~2.3 Mbit/s just after 8 Mbit/s went
/// through cleanly), and cutting to it would throttle good links.
/// Always within [min, max]. Thread-safe: reports arrive on a network thread.
/// </summary>
public sealed class BitrateController
{
    private readonly int _minKbps;
    private readonly int _maxKbps;
    private readonly Lock _lock = new();
    private double _target;
    private double _sentKbps;
    private int? _lastRemb;
    private double? _lastLoss;

    public BitrateController(int minKbps, int maxKbps, int startKbps)
    {
        _minKbps = Math.Max(50, minKbps);
        _maxKbps = Math.Max(_minKbps, maxKbps);
        _target = Math.Clamp(startKbps, _minKbps, _maxKbps);
    }

    public int TargetKbps
    {
        get
        {
            lock (_lock)
            {
                return (int)Math.Round(_target);
            }
        }
    }

    /// <summary>The last REMB and loss the glasses reported, for the stats.</summary>
    public (int? RembKbps, double? LossFraction) LastFeedback
    {
        get
        {
            lock (_lock)
            {
                return (_lastRemb, _lastLoss);
            }
        }
    }

    /// <summary>What the pump sent over the last second.</summary>
    public void OnSent(double kbps)
    {
        lock (_lock)
        {
            _sentKbps = kbps;
        }
    }

    /// <param name="adapt">False records the feedback for the stats without acting on it (link test).</param>
    public void OnFeedback(ReceiverFeedback feedback, bool adapt = true)
    {
        lock (_lock)
        {
            if (feedback.LossFraction is { } loss)
            {
                _lastLoss = loss;
                if (adapt && loss > 0.10)
                {
                    _target *= 1 - 0.5 * loss;
                }
                else if (adapt && loss < 0.02 && _sentKbps >= 0.5 * _target)
                {
                    _target *= 1.08;
                }
            }

            if (feedback.RembKbps is { } remb)
            {
                _lastRemb = remb;
            }

            _target = Math.Clamp(_target, _minKbps, _maxKbps);
        }
    }
}
