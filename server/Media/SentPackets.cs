using System.Diagnostics;

namespace GlassesRemote.Server.Media;

/// <summary>
/// The last packets sent, by sequence number, so the ones the glasses NACK can be sent again.
/// Up to 3 s old: on a congested link NACKs arrived over a second after the packet was sent,
/// when a 1 s window had already dropped it. Each is resent at most once per
/// <see cref="MinResendGap"/>: the glasses repeat a NACK roughly once per round trip until the
/// packet arrives. 4096 slots cover 3 s up to ~16 Mbit/s.
/// </summary>
public sealed class SentPackets(int capacity = 4096)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan MinResendGap = TimeSpan.FromMilliseconds(50);

    private readonly Slot?[] _slots = new Slot?[capacity];
    private readonly Lock _lock = new();

    private sealed class Slot(ushort seq, RtpPacket packet, long sentAt)
    {
        public ushort Seq { get; } = seq;

        public RtpPacket Packet { get; } = packet;

        public long SentAt { get; } = sentAt;

        public long ResentAt { get; set; }
    }

    public void Add(ushort seq, RtpPacket packet, long now)
    {
        lock (_lock)
        {
            _slots[seq % _slots.Length] = new Slot(seq, packet, now);
        }
    }

    /// <summary>The packets to send again for these NACKed sequence numbers, marked as resent.</summary>
    public List<RtpPacket> TakeForResend(IEnumerable<ushort> lost, long now)
    {
        var resend = new List<RtpPacket>();
        lock (_lock)
        {
            foreach (var seq in lost.Distinct())
            {
                var slot = _slots[seq % _slots.Length];
                if (slot is null || slot.Seq != seq
                    || Stopwatch.GetElapsedTime(slot.SentAt, now) > MaxAge
                    || (slot.ResentAt != 0 && Stopwatch.GetElapsedTime(slot.ResentAt, now) < MinResendGap))
                {
                    continue;
                }

                slot.ResentAt = now;
                resend.Add(slot.Packet with { ResendSeq = seq });
            }
        }
        return resend;
    }
}
