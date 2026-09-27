using System.Collections.Concurrent;
using System.Diagnostics;
using GlassesRemote.Server.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>A frame's packets are spread out at the pacing rate, in order, with bounded delay.</summary>
public sealed class RtpPacerTests
{
    private readonly ConcurrentQueue<(int Index, long At)> _sent = new();

    private RtpPacer Pacer(int minKbps, int maxDelayMs) =>
        new(p => _sent.Enqueue((p.Payload[0], Stopwatch.GetTimestamp())), minKbps,
            TimeSpan.FromMilliseconds(maxDelayMs), NullLogger.Instance);

    /// <summary>1250-byte packets: 10 kbit each, so 10 ms apart at 1000 kbps.</summary>
    private static List<RtpPacket> Frame(int packets) =>
        Enumerable.Range(0, packets)
            .Select(i => new RtpPacket(Enumerable.Repeat((byte)i, 1250).ToArray(), 0, i == packets - 1))
            .ToList();

    private async Task<double> SpreadMsOnceSent(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_sent.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"only {_sent.Count} of {count} sent");
            await Task.Delay(5);
        }
        var times = _sent.Select(s => s.At).ToArray();
        return Stopwatch.GetElapsedTime(times[0], times[^1]).TotalMilliseconds;
    }

    [Fact]
    public async Task Packets_go_out_in_order_at_the_pacing_rate()
    {
        using var pacer = Pacer(minKbps: 1000, maxDelayMs: 10_000);

        pacer.Enqueue(Frame(5));

        var spread = await SpreadMsOnceSent(5);
        Assert.Equal([0, 1, 2, 3, 4], _sent.Select(s => s.Index));
        Assert.InRange(spread, 30, 150); // four 10 ms gaps
        var delay = pacer.TakeSendDelay();
        Assert.InRange(delay.MaxMs, 30, 150);
        Assert.Equal(0, pacer.TakeSendDelay().MaxMs); // taken
    }

    [Fact]
    public async Task A_backlog_speeds_the_pacer_up_to_keep_within_the_max_delay()
    {
        // 100 kbps alone would take 400 ms; 50 kbit within 50 ms needs 1 Mbps.
        using var pacer = Pacer(minKbps: 100, maxDelayMs: 50);

        pacer.Enqueue(Frame(5));

        Assert.InRange(await SpreadMsOnceSent(5), 20, 150);
    }

    [Fact]
    public void Without_a_rate_packets_go_out_at_once()
    {
        using var pacer = Pacer(minKbps: 0, maxDelayMs: 150);

        pacer.Enqueue(Frame(5));

        Assert.Equal([0, 1, 2, 3, 4], _sent.Select(s => s.Index));
    }
}
