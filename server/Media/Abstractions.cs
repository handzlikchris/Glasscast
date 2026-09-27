namespace GlassesRemote.Server.Media;

/// <summary>Encodes raw BGRA frames for the WebRTC track.</summary>
public interface IFrameEncoder : IDisposable
{
    /// <summary>Codec name as used in SDP, e.g. "VP8" or "H264".</summary>
    string Codec { get; }

    /// <summary>Encoded frame, or null if the encoder produced nothing for this input.</summary>
    byte[]? Encode(byte[] bgra, int width, int height);

    void ForceKeyFrame();

    /// <summary>Changes the target bitrate from the next frame on (mid-stream, no keyframe).</summary>
    void SetTargetKbps(int kbps);
}

/// <summary>A send-only WebRTC video peer for one session.</summary>
public interface IMediaPeer : IDisposable
{
    /// <summary>Raised once the peer is connected and frames can flow.</summary>
    event Action? Connected;

    /// <summary>Raised when the connection fails or closes.</summary>
    event Action? Closed;

    /// <summary>
    /// The glasses can't decode the picture (lost packets, or the stream just started) and ask for
    /// a keyframe (RTCP PLI or FIR). Raised on a network thread.
    /// </summary>
    event Action? KeyframeRequested;

    /// <summary>The glasses' receiver reports: loss and bandwidth estimate. Raised on a network thread.</summary>
    event Action<ReceiverFeedback>? FeedbackReceived;

    bool IsConnected { get; }

    /// <summary>Creates the SDP offer (with our public-IP candidate) to send to the glasses.</summary>
    Task<string> CreateOfferAsync();

    bool ApplyAnswer(string sdp);

    void AddRemoteCandidate(string candidate, string? sdpMid, int sdpMLineIndex);

    /// <summary>
    /// Sends one encoded frame. <paramref name="durationRtpUnits"/> is at the 90 kHz video clock.
    /// Returns the RTP timestamp the frame went out with, so the glasses can match it to its capture time.
    /// </summary>
    uint SendFrame(byte[] encoded, uint durationRtpUnits);

    /// <summary>Pacing delay and retransmissions since the last call.</summary>
    SendStats TakeSendStats();
}

/// <summary>
/// Since the last call: how long frames waited in the pacer (handed over → last packet sent),
/// how many packets the glasses NACKed, and how many of those were sent again.
/// </summary>
public readonly record struct SendStats(double AvgMs, double MaxMs, int Nacked, int Resent);

public interface IMediaPeerFactory
{
    IMediaPeer Create(string codec);
}

public interface IFrameEncoderFactory
{
    IFrameEncoder Create();
}
