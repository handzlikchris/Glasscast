using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Windows;

namespace GlassesRemote.Server.Tests.Windows;

/// <summary>
/// The real Windows process loopback, opened on this test process only (which plays nothing), so
/// the test is silent. That it records at full level whatever the PC's volume was checked by hand
/// on 2026-09-27: a tone at -34 dBFS came back at -34 dBFS with the PC at 22 % (-22.9 dB), where
/// endpoint loopback got it 22.9 dB quieter.
/// </summary>
public sealed class ProcessLoopbackCaptureTests
{
    [Fact]
    public void Opens_through_the_virtual_device_and_reads_without_error()
    {
        Assert.True(ProcessLoopbackCapture.IsSupported, "this PC's Windows predates process loopback");

        using var capture = ProcessLoopbackCapture.Start(includeOnly: true);
        var fifo = new AudioFifo();
        for (var i = 0; i < 20; i++)
        {
            capture.ReadInto(fifo);
            Thread.Sleep(5);
        }

        Assert.False(capture.IsStale);
        // Nothing plays in this process, so whatever arrived is silence (typically nothing at all).
        var samples = new float[fifo.Frames * 2];
        fifo.Read(samples);
        Assert.True(OpusAudioEncoder.IsDigitalSilence(samples));
    }
}
