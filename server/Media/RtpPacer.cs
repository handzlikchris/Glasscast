using System.Diagnostics;
using GlassesRemote.Server.Windows;

namespace GlassesRemote.Server.Media;

/// <summary>How long frames waited in the pacer: handed over → last packet sent.</summary>
public readonly record struct SendDelay(double AvgMs, double MaxMs);

/// <summary>
/// Sends RTP packets at a steady rate instead of a whole frame at once. A keyframe is 40-90 KB,
/// 35-75 packets; sent back to back they arrive at the slowest hop (mobile cell, the phone's
/// link to the glasses) faster than it drains and its queue drops some, which breaks the frame
/// and freezes the picture until the next keyframe. Spread out, the same packets get through.
///
/// The rate never drops below <c>minKbps</c>, and rises when needed to send everything queued
/// within <c>maxDelay</c>, so a large frame costs at most that much extra latency.
/// With <c>minKbps</c> 0 packets are sent at once, as before.
/// </summary>
public sealed class RtpPacer : IDisposable
{
    /// <summary>Waits shorter than this aren't worth a sleep; the packet goes a little early.</summary>
    private static readonly TimeSpan MinSleep = TimeSpan.FromMilliseconds(0.2);

    /// <summary>After an oversleep the pacer may catch up by this much in one go, no more.</summary>
    private static readonly long CatchUp = Stopwatch.Frequency * 5 / 1000;

    private readonly Action<RtpPacket> _send;
    private readonly ILogger _logger;
    private readonly double _minBitsPerSecond;
    private readonly double _maxDelaySeconds;
    private readonly Queue<Entry> _queue = new();
    private readonly Queue<Entry> _urgent = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Thread? _thread;
    private volatile bool _stopped;
    private long _queuedBytes;
    private double _bitsPerSecond;
    private double _delaySumMs;
    private double _delayMaxMs;
    private int _delayCount;

    private readonly record struct Entry(RtpPacket Packet, long EnqueuedAt, bool EndsFrame);

    public RtpPacer(Action<RtpPacket> send, int minKbps, TimeSpan maxDelay, ILogger logger)
    {
        _send = send;
        _logger = logger;
        _minBitsPerSecond = Math.Max(0, minKbps) * 1000.0;
        _maxDelaySeconds = Math.Max(0.01, maxDelay.TotalSeconds);
        if (_minBitsPerSecond > 0)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "RTP pacer", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>Queues one frame's packets, in order, behind any not yet sent.</summary>
    public void Enqueue(IReadOnlyList<RtpPacket> frame)
    {
        if (frame.Count == 0 || _stopped)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_thread is null)
        {
            foreach (var packet in frame)
            {
                SendSafely(packet);
            }
            RecordDelay(now, Stopwatch.GetTimestamp());
            return;
        }

        lock (_lock)
        {
            _queuedBytes += frame.Sum(p => (long)p.Payload.Length);
            _bitsPerSecond = Math.Max(_minBitsPerSecond, _queuedBytes * 8 / _maxDelaySeconds);
            for (var i = 0; i < frame.Count; i++)
            {
                _queue.Enqueue(new Entry(frame[i], now, i == frame.Count - 1));
            }
        }
        _available.Release(frame.Count);
    }

    /// <summary>
    /// Queues packets ahead of any frame not yet sent (retransmissions: the glasses are waiting
    /// for them). Paced like the rest; without pacing they go out at once.
    /// </summary>
    public void EnqueueUrgent(IReadOnlyList<RtpPacket> packets)
    {
        if (packets.Count == 0 || _stopped)
        {
            return;
        }

        if (_thread is null)
        {
            foreach (var packet in packets)
            {
                SendSafely(packet);
            }
            return;
        }

        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _queuedBytes += packets.Sum(p => (long)p.Payload.Length);
            _bitsPerSecond = Math.Max(_minBitsPerSecond, _queuedBytes * 8 / _maxDelaySeconds);
            foreach (var packet in packets)
            {
                _urgent.Enqueue(new Entry(packet, now, EndsFrame: false));
            }
        }
        _available.Release(packets.Count);
    }

    /// <summary>Frame delays since the last call.</summary>
    public SendDelay TakeSendDelay()
    {
        lock (_lock)
        {
            var result = new SendDelay(_delayCount == 0 ? 0 : _delaySumMs / _delayCount, _delayMaxMs);
            _delaySumMs = 0;
            _delayMaxMs = 0;
            _delayCount = 0;
            return result;
        }
    }

    private void Run()
    {
        using var sleep = new PreciseSleep();
        long nextAt = 0;
        while (true)
        {
            _available.Wait();
            if (_stopped)
            {
                return;
            }

            // Wait for the slot first, then pick the packet: an urgent one queued meanwhile goes next.
            var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextAt);
            if (wait >= MinSleep)
            {
                sleep.Sleep(wait);
            }

            Entry entry;
            double bitsPerSecond;
            lock (_lock)
            {
                entry = _urgent.Count > 0 ? _urgent.Dequeue() : _queue.Dequeue();
                bitsPerSecond = _bitsPerSecond;
            }

            SendSafely(entry.Packet);
            var now = Stopwatch.GetTimestamp();
            var bytes = entry.Packet.Payload.Length;
            lock (_lock)
            {
                _queuedBytes -= bytes;
            }
            if (entry.EndsFrame)
            {
                RecordDelay(entry.EnqueuedAt, now);
            }

            // Schedule from when this packet was due, so an oversleep is made up (a little);
            // after an idle spell, from now.
            nextAt = Math.Max(nextAt, now - CatchUp) + (long)(bytes * 8 / bitsPerSecond * Stopwatch.Frequency);
        }
    }

    private void SendSafely(RtpPacket packet)
    {
        try
        {
            _send(packet);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RTP packet not sent");
        }
    }

    private void RecordDelay(long from, long to)
    {
        var ms = Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        lock (_lock)
        {
            _delaySumMs += ms;
            _delayMaxMs = Math.Max(_delayMaxMs, ms);
            _delayCount++;
        }
    }

    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        _available.Release();
        _thread?.Join(TimeSpan.FromSeconds(1));
    }
}
