using System.Diagnostics;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Windows;

namespace GlassesRemote.Server.Media;

/// <summary>
/// About a second of the audio pump: whether sound is being captured and sent, the Opus bytes
/// sent as kbit/s (payload only, like the video's figure) and how many packets that took.
/// </summary>
public readonly record struct AudioStats(bool On, double Kbps, int Packets);

/// <summary>
/// The PC's sound to the glasses, for one session: loopback capture → <see cref="AudioTimeline"/>
/// → Opus → the peer's audio track. Off until the glasses turn it on (<see cref="Enabled"/>);
/// while off nothing is captured or sent.
///
/// Runs on a thread of its own, waking every 5 ms, since Windows hands over captured sound in
/// 10 ms pieces and a 20 ms Opus frame should leave as soon as it's complete. The capture is
/// opened on this thread (COM) and reopened when the default output device changes or fails.
/// </summary>
public sealed class AudioPump : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan RetryStart = TimeSpan.FromSeconds(2);

    /// <summary>Captured sound kept ahead of the frame being sent, against late capture packets.</summary>
    private const int PrebufferMs = 10;

    /// <summary>More than this buffered is trimmed (see <see cref="AudioTimeline"/>).</summary>
    private const int MaxBufferedMs = 80;

    /// <summary>Silent frames sent before going quiet, so the glasses' decoder gets the sound's tail.</summary>
    private const int SilentFramesSent = 2;

    /// <summary>While silent, one packet this often (like Opus DTX's refresh) keeps the glasses' decoder in step.</summary>
    private const int SilenceRefreshMs = 400;

    private readonly IAudioCaptureFactory _captures;
    private readonly IMediaPeer _peer;
    private readonly ILogger _logger;
    private readonly OpusAudioEncoder _encoder;
    private readonly AudioTimeline _timeline;
    private readonly AudioFifo _fifo = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly uint _rtpBase = (uint)Random.Shared.NextInt64(0, uint.MaxValue);
    private volatile bool _enabled;
    private volatile bool _stopped;
    private int _capturing;
    private int _lossPercent;
    private long _bytes;
    private int _packets;
    private long _statsFrom = Stopwatch.GetTimestamp();
    private int _failures;
    private int _silentFrames;

    public AudioPump(IAudioCaptureFactory captures, AudioOptions options, IMediaPeer peer, ILogger logger)
    {
        _captures = captures;
        _peer = peer;
        _logger = logger;
        _encoder = new OpusAudioEncoder(options.Kbps, options.FrameMs);
        _timeline = new AudioTimeline(_encoder.FrameSamples, Samples(PrebufferMs), Samples(MaxBufferedMs));
        _peer.FeedbackReceived += OnFeedback;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio pump", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The glasses' ♪ button. Starts off: the glasses say what they want right after hello.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _wake.Set();
        }
    }

    /// <summary>Capturing and sending right now: on, and the PC has an output device to capture.</summary>
    public bool Capturing => Volatile.Read(ref _capturing) == 1;

    /// <summary>What was sent since the last call.</summary>
    public AudioStats TakeStats()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(Interlocked.Exchange(ref _statsFrom, now), now).TotalSeconds;
        var bytes = Interlocked.Exchange(ref _bytes, 0);
        var packets = Interlocked.Exchange(ref _packets, 0);
        return new AudioStats(Capturing, seconds > 0 ? bytes * 8 / seconds / 1000 : 0, packets);
    }

    /// <summary>The glasses' loss (from their video reports: the same path) sets how much FEC Opus adds.</summary>
    private void OnFeedback(ReceiverFeedback feedback)
    {
        if (feedback.LossFraction is { } loss)
        {
            Volatile.Write(ref _lossPercent, (int)Math.Round(Math.Clamp(loss, 0, 1) * 100));
        }
    }

    private static int Samples(int ms) => AudioFormat48k.SampleRate / 1000 * ms;

    /// <summary>Samples per channel since the pump started, on the wall clock.</summary>
    private long Now() => Stopwatch.GetElapsedTime(_startedAt).Ticks * AudioFormat48k.SampleRate / TimeSpan.TicksPerSecond;

    private void Run()
    {
        using var sleep = new PreciseSleep();
        var frame = new float[_encoder.FrameSamples * AudioFormat48k.Channels];
        IAudioCapture? capture = null;
        var nextStart = 0L;
        var lastSent = false;
        try
        {
            while (!_stopped)
            {
                if (!_enabled)
                {
                    Close(ref capture);
                    _wake.Reset();
                    if (!_enabled && !_stopped)
                    {
                        _wake.Wait();
                    }
                    continue;
                }

                try
                {
                    if (capture is { IsStale: true })
                    {
                        _logger.LogInformation("Default audio output changed; capturing the new one");
                        Close(ref capture);
                    }
                    if (capture is null)
                    {
                        if (Stopwatch.GetTimestamp() < nextStart || (capture = _captures.Start()) is null)
                        {
                            nextStart = Math.Max(nextStart, Stopwatch.GetTimestamp() + (long)(RetryStart.TotalSeconds * Stopwatch.Frequency));
                            _wake.Wait(TimeSpan.FromMilliseconds(100));
                            continue;
                        }
                        _fifo.Clear();
                        _timeline.Restart(Now());
                        lastSent = false;
                        Volatile.Write(ref _capturing, 1);
                        _logger.LogInformation("Audio on: capturing the PC's sound");
                    }

                    capture.ReadInto(_fifo);
                    var now = Now();
                    while (_timeline.TryNextFrame(now, _fifo, frame, out var position, out _))
                    {
                        lastSent = SendFrame(frame, position, lastSent);
                    }
                }
                catch (Exception ex)
                {
                    // Device unplugged or disabled mid-capture, or a bug: log (the first few), start over shortly.
                    if (Interlocked.Increment(ref _failures) <= 5)
                    {
                        _logger.LogWarning(ex, "Audio capture failed; retrying");
                    }
                    Close(ref capture);
                    nextStart = Stopwatch.GetTimestamp() + (long)(RetryStart.TotalSeconds * Stopwatch.Frequency);
                    continue;
                }

                sleep.Sleep(Tick);
            }
        }
        finally
        {
            Close(ref capture);
        }
    }

    /// <summary>Encodes one frame and sends it unless it's DTX; returns whether it was sent.</summary>
    private bool SendFrame(float[] frame, long position, bool lastSent)
    {
        if (!_peer.IsConnected)
        {
            return false;
        }

        // Every frame is encoded, silent or not, so the encoder's state follows the sound.
        _silentFrames = OpusAudioEncoder.IsDigitalSilence(frame) ? _silentFrames + 1 : 0;
        _encoder.PacketLossPercent = Volatile.Read(ref _lossPercent);
        var packet = _encoder.Encode(frame);
        var quiet = _silentFrames > SilentFramesSent && _silentFrames % (SilenceRefreshMs / _encoder.FrameMs) != 0;
        if (OpusAudioEncoder.IsDtx(packet) || quiet)
        {
            return false;
        }

        // The marker bit starts a talkspurt: the first packet after silence (RFC 3551).
        _peer.SendAudio(packet.ToArray(), unchecked(_rtpBase + (uint)position), marker: !lastSent);
        Interlocked.Add(ref _bytes, packet.Length);
        Interlocked.Increment(ref _packets);
        return true;
    }

    private void Close(ref IAudioCapture? capture)
    {
        if (capture is not null)
        {
            try
            {
                capture.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Audio capture didn't close cleanly");
            }
            capture = null;
            _logger.LogInformation("Audio off");
        }
        Volatile.Write(ref _capturing, 0);
    }

    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        _peer.FeedbackReceived -= OnFeedback;
        _wake.Set();
        // _wake isn't disposed: should the join time out, the thread may still touch it.
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
