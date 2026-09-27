using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Fakes;

/// <summary>Pretends to be a WebRTC peer: "connects" as soon as an answer is applied.</summary>
public sealed class FakePeer : IMediaPeer
{
    private int _frames;

    public event Action? Connected;

    public event Action? Closed;

    public event Action? KeyframeRequested;

    public event Action<ReceiverFeedback>? FeedbackReceived;

    /// <summary>Acts like the glasses' receiver report.</summary>
    public void ReportFeedback(ReceiverFeedback feedback) => FeedbackReceived?.Invoke(feedback);

    public bool IsConnected { get; private set; }

    /// <summary>Acts like the glasses sending a PLI.</summary>
    public void RequestKeyframe() => KeyframeRequested?.Invoke();

    public string? AppliedAnswer { get; private set; }

    public int FramesSent => Volatile.Read(ref _frames);

    public bool Disposed { get; private set; }

    /// <summary>Whether the last offer included the PC's LAN address (glasses at home).</summary>
    public bool? OfferedLan { get; private set; }

    public Task<string> CreateOfferAsync(bool offerLan)
    {
        OfferedLan = offerLan;
        return Task.FromResult("v=0\r\nfake-offer\r\n");
    }

    public System.Net.IPEndPoint? RemoteMediaEndPoint { get; set; }

    public bool ApplyAnswer(string sdp)
    {
        AppliedAnswer = sdp;
        IsConnected = true;
        Connected?.Invoke();
        return true;
    }

    public void AddRemoteCandidate(string candidate, string? sdpMid, int sdpMLineIndex)
    {
    }

    public uint SendFrame(byte[] encoded, uint durationRtpUnits) =>
        (uint)(Interlocked.Increment(ref _frames) - 1) * durationRtpUnits;

    public SendStats TakeSendStats() => new(1, 2, 3, 2);

    public bool CarriesAudio { get; init; } = true;

    /// <summary>Opus packets sent: payload, RTP timestamp, marker.</summary>
    public List<(byte[] Opus, uint Timestamp, bool Marker)> AudioSent { get; } = new();

    public int AudioPacketsSent
    {
        get
        {
            lock (AudioSent)
            {
                return AudioSent.Count;
            }
        }
    }

    public void SendAudio(byte[] opus, uint rtpTimestamp, bool marker)
    {
        lock (AudioSent)
        {
            AudioSent.Add((opus, rtpTimestamp, marker));
        }
    }

    public void Dispose()
    {
        Disposed = true;
        IsConnected = false;
        Closed?.Invoke();
    }
}

public sealed class FakePeerFactory : IMediaPeerFactory
{
    public List<FakePeer> Created { get; } = new();

    public IMediaPeer Create(string codec)
    {
        var peer = new FakePeer();
        lock (Created)
        {
            Created.Add(peer);
        }
        return peer;
    }
}

public sealed class FakeEncoder : IFrameEncoder
{
    private int _keyframes;

    public string Codec => "VP8";

    public int KeyframesForced => Volatile.Read(ref _keyframes);

    public byte[]? Encode(byte[] bgra, int width, int height) => [1, 2, 3];

    public void ForceKeyFrame() => Interlocked.Increment(ref _keyframes);

    public int TargetKbps { get; private set; }

    public void SetTargetKbps(int kbps) => TargetKbps = kbps;

    public void Dispose()
    {
    }
}

public sealed class FakeEncoderFactory : IFrameEncoderFactory
{
    public IFrameEncoder Create() => new FakeEncoder();
}

/// <summary>
/// Pretends to capture the PC's sound: a steady 440 Hz tone, delivered in 10 ms pieces as the
/// wall clock passes (like WASAPI), or nothing while <see cref="Silent"/> (nothing playing).
/// </summary>
public sealed class FakeAudioCaptureFactory : IAudioCaptureFactory
{
    private int _started;

    public int Started => Volatile.Read(ref _started);

    /// <summary>Currently open captures (0 or 1).</summary>
    public int Open => Volatile.Read(ref _open);

    private int _open;

    public bool Silent { get; set; }

    /// <summary>Like a PC without any output device.</summary>
    public bool NoDevice { get; set; }

    public IAudioCapture? Start()
    {
        if (NoDevice)
        {
            return null;
        }
        Interlocked.Increment(ref _started);
        Interlocked.Increment(ref _open);
        return new Capture(this);
    }

    private sealed class Capture(FakeAudioCaptureFactory owner) : IAudioCapture
    {
        private const int Piece = AudioFormat48k.SampleRate / 100;
        private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
        private long _delivered;

        public bool IsStale => false;

        public void ReadInto(AudioFifo fifo)
        {
            var due = System.Diagnostics.Stopwatch.GetElapsedTime(_start).Ticks * AudioFormat48k.SampleRate / TimeSpan.TicksPerSecond;
            var buffer = new float[Piece * 2];
            while (_delivered + Piece <= due)
            {
                for (var i = 0; i < Piece; i++)
                {
                    var v = owner.Silent ? 0f : 0.3f * MathF.Sin(2 * MathF.PI * 440 * (_delivered + i) / AudioFormat48k.SampleRate);
                    buffer[2 * i] = buffer[2 * i + 1] = v;
                }
                if (!owner.Silent)
                {
                    fifo.Write(buffer);
                }
                _delivered += Piece;
            }
        }

        public void Dispose() => Interlocked.Decrement(ref owner._open);
    }
}

public sealed class FakeCapture : ICaptureSource
{
    public List<PixelRect> Sources { get; } = new();

    /// <summary>Thrown from the next capture, like a failing capture or encoder.</summary>
    public Exception? Failure { get; set; }

    public bool TryCapture(PixelRect source, PixelSize frame, byte[] bgra)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        lock (Sources)
        {
            Sources.Add(source);
        }
        return true;
    }

    public void Dispose()
    {
    }
}
