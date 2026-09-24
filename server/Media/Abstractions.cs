namespace GlassesRemote.Server.Media;

/// <summary>Encodes raw BGRA frames for the WebRTC track.</summary>
public interface IFrameEncoder : IDisposable
{
    /// <summary>Codec name as used in SDP, e.g. "VP8" or "H264".</summary>
    string Codec { get; }

    /// <summary>Encoded frame, or null if the encoder produced nothing for this input.</summary>
    byte[]? Encode(byte[] bgra, int width, int height);

    void ForceKeyFrame();
}

/// <summary>A send-only WebRTC video peer for one session.</summary>
public interface IMediaPeer : IDisposable
{
    /// <summary>Raised once the peer is connected and frames can flow.</summary>
    event Action? Connected;

    /// <summary>Raised when the connection fails or closes.</summary>
    event Action? Closed;

    bool IsConnected { get; }

    /// <summary>Creates the SDP offer (with our public-IP candidate) to send to the glasses.</summary>
    Task<string> CreateOfferAsync();

    bool ApplyAnswer(string sdp);

    void AddRemoteCandidate(string candidate, string? sdpMid, int sdpMLineIndex);

    /// <summary>Sends one encoded frame. <paramref name="durationRtpUnits"/> is at the 90 kHz video clock.</summary>
    void SendFrame(byte[] encoded, uint durationRtpUnits);
}

public interface IMediaPeerFactory
{
    IMediaPeer Create(string codec);
}

public interface IFrameEncoderFactory
{
    IFrameEncoder Create();
}
