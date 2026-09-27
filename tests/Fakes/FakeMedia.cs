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

    public bool IsConnected { get; private set; }

    /// <summary>Acts like the glasses sending a PLI.</summary>
    public void RequestKeyframe() => KeyframeRequested?.Invoke();

    public string? AppliedAnswer { get; private set; }

    public int FramesSent => Volatile.Read(ref _frames);

    public bool Disposed { get; private set; }

    public Task<string> CreateOfferAsync() => Task.FromResult("v=0\r\nfake-offer\r\n");

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

    public void Dispose()
    {
    }
}

public sealed class FakeEncoderFactory : IFrameEncoderFactory
{
    public IFrameEncoder Create() => new FakeEncoder();
}

public sealed class FakeCapture : ICaptureSource
{
    public List<PixelRect> Sources { get; } = new();

    public bool TryCapture(PixelRect source, PixelSize frame, byte[] bgra)
    {
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
