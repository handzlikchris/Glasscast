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
        sent.Add(65535, Packet(1), Second, epoch: 0);
        sent.Add(0, Packet(2), Second, epoch: 0);

        var resend = sent.TakeForResend([0, 65535, 7], Second, epoch: 0);

        Assert.Equal([(ushort)0, (ushort)65535], resend.Select(p => p.ResendSeq!.Value));
        Assert.Equal([2, 1], resend.Select(p => (int)p.Payload[0]));
    }

    [Fact]
    public void Not_again_within_the_gap_and_not_once_it_is_old()
    {
        var sent = new SentPackets();
        sent.Add(5, Packet(1), Second, epoch: 0);

        Assert.Single(sent.TakeForResend([5], Second, epoch: 0));
        Assert.Empty(sent.TakeForResend([5], Second + Second / 100, epoch: 0)); // 10 ms later
        Assert.Single(sent.TakeForResend([5], Second + Second / 10, epoch: 0)); // 100 ms later
        Assert.Empty(sent.TakeForResend([5], 5 * Second, epoch: 0));
    }

    [Fact]
    public void A_newer_packet_in_the_same_slot_isnt_mistaken_for_the_old_one()
    {
        var sent = new SentPackets(capacity: 4);
        sent.Add(1, Packet(1), Second, epoch: 0);
        sent.Add(5, Packet(5), Second, epoch: 0);

        Assert.Empty(sent.TakeForResend([1], Second, epoch: 0));
        Assert.Single(sent.TakeForResend([5], Second, epoch: 0));
    }

    [Fact]
    public void Nothing_from_before_a_sequence_wrap_is_resent()
    {
        // SIPSorcery's SRTP rollover counter moves on when 65535 is encrypted: resending it (or
        // anything older) after that would desynchronise the glasses for good.
        var sent = new SentPackets();
        sent.Add(65535, Packet(1), Second, epoch: 0);
        sent.Add(0, Packet(2), Second, epoch: 1);

        var resend = sent.TakeForResend([65535, 0], Second, epoch: 1);

        Assert.Equal([(ushort)0], resend.Select(p => p.ResendSeq!.Value));
    }
}
