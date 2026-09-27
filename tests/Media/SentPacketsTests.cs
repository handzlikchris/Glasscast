using System.Diagnostics;
using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>NACKed packets come back with their sequence number, while recent, and not too often.</summary>
public sealed class SentPacketsTests
{
    private static readonly long Second = Stopwatch.Frequency;

    private static RtpPacket Packet(byte id) => new([id], 90_000, false);

    [Fact]
    public void A_nacked_packet_comes_back_with_its_sequence_number()
    {
        var sent = new SentPackets();
        sent.Add(65535, Packet(1), Second);
        sent.Add(0, Packet(2), Second);

        var resend = sent.TakeForResend([0, 65535, 7], Second);

        Assert.Equal([(ushort)0, (ushort)65535], resend.Select(p => p.ResendSeq!.Value));
        Assert.Equal([2, 1], resend.Select(p => (int)p.Payload[0]));
    }

    [Fact]
    public void Not_again_within_the_gap_and_not_once_it_is_old()
    {
        var sent = new SentPackets();
        sent.Add(5, Packet(1), Second);

        Assert.Single(sent.TakeForResend([5], Second));
        Assert.Empty(sent.TakeForResend([5], Second + Second / 100)); // 10 ms later
        Assert.Single(sent.TakeForResend([5], Second + Second / 10)); // 100 ms later
        Assert.Empty(sent.TakeForResend([5], 3 * Second));
    }

    [Fact]
    public void A_newer_packet_in_the_same_slot_isnt_mistaken_for_the_old_one()
    {
        var sent = new SentPackets(capacity: 4);
        sent.Add(1, Packet(1), Second);
        sent.Add(5, Packet(5), Second);

        Assert.Empty(sent.TakeForResend([1], Second));
        Assert.Single(sent.TakeForResend([5], Second));
    }
}
