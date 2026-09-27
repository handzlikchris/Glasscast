using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>The audio pump in real time, with a fake capture (a tone) and a fake peer.</summary>
public sealed class AudioPumpTests
{
    private readonly FakeAudioCaptureFactory _captures = new();
    private readonly FakePeer _peer = new();

    private AudioPump Pump() => new(_captures, new AudioOptions(), _peer, NullLogger.Instance);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Captures_and_sends_nothing_until_turned_on()
    {
        _peer.ApplyAnswer("v=0");
        using var pump = Pump();

        await Task.Delay(150);

        Assert.Equal(0, _captures.Started);
        Assert.Equal(0, _peer.AudioPacketsSent);
        Assert.False(pump.Capturing);
    }

    [Fact]
    public async Task Sends_20ms_opus_packets_on_the_48k_clock_and_stops_capturing_when_turned_off()
    {
        _peer.ApplyAnswer("v=0");
        using var pump = Pump();

        pump.Enabled = true;
        await WaitUntil(() => _peer.AudioPacketsSent >= 10);
        pump.Enabled = false;
        await WaitUntil(() => _captures.Open == 0);

        List<(byte[] Opus, uint Timestamp, bool Marker)> sent;
        lock (_peer.AudioSent)
        {
            sent = _peer.AudioSent.ToList();
        }
        Assert.True(sent[0].Marker); // the first packet of a talkspurt
        Assert.All(sent.Skip(1), p => Assert.False(p.Marker));
        Assert.All(sent.Zip(sent.Skip(1)), pair => Assert.Equal(960u, unchecked(pair.Second.Timestamp - pair.First.Timestamp)));
        Assert.All(sent, p => Assert.InRange(p.Opus.Length, 3, 400));
        Assert.False(pump.Capturing);

        var stats = pump.TakeStats();
        Assert.True(stats.Packets >= 10);
        Assert.True(stats.Kbps > 0);
    }

    [Fact]
    public async Task Silence_sends_next_to_nothing_and_time_keeps_running()
    {
        _peer.ApplyAnswer("v=0");
        _captures.Silent = true;
        using var pump = Pump();

        pump.Enabled = true;
        await Task.Delay(600);
        var duringSilence = _peer.AudioPacketsSent;
        _captures.Silent = false;
        await WaitUntil(() => _peer.AudioPacketsSent >= duringSilence + 5);

        Assert.InRange(duringSilence, 0, 8); // DTX: a few while Opus notices, then a refresh now and then
        (byte[] Opus, uint Timestamp, bool Marker) first;
        lock (_peer.AudioSent)
        {
            first = _peer.AudioSent[duringSilence];
        }
        Assert.True(first.Marker); // sound starts again after silence
    }

    [Fact]
    public async Task Nothing_goes_out_before_the_peer_connects()
    {
        using var pump = Pump();
        pump.Enabled = true;
        await WaitUntil(() => pump.Capturing);
        await Task.Delay(100);

        Assert.Equal(0, _peer.AudioPacketsSent);
    }

    [Fact]
    public async Task A_pc_without_an_output_device_just_has_no_sound()
    {
        _peer.ApplyAnswer("v=0");
        _captures.NoDevice = true;
        using var pump = Pump();

        pump.Enabled = true;
        await Task.Delay(200);

        Assert.False(pump.Capturing);
        Assert.Equal(0, _peer.AudioPacketsSent);
        Assert.False(pump.TakeStats().On);
    }
}
