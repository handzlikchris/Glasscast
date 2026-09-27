using System.Net;

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

    /// <summary>
    /// Creates the SDP offer (with our public-IP candidate) to send to the glasses; with
    /// <paramref name="offerLan"/> the PC's LAN address too, first (glasses at home).
    /// </summary>
    Task<string> CreateOfferAsync(bool offerLan);

    /// <summary>Where the video goes once connected (the glasses' side of the chosen pair), else null.</summary>
    IPEndPoint? RemoteMediaEndPoint { get; }

    bool ApplyAnswer(string sdp);

    void AddRemoteCandidate(string candidate, string? sdpMid, int sdpMLineIndex);

    /// <summary>
    /// Sends one encoded frame. <paramref name="durationRtpUnits"/> is at the 90 kHz video clock.
    /// Returns the RTP timestamp the frame went out with, so the glasses can match it to its capture time.
    /// </summary>
    uint SendFrame(byte[] encoded, uint durationRtpUnits);

    /// <summary>Pacing delay and retransmissions since the last call.</summary>
    SendStats TakeSendStats();

    /// <summary>Whether the offer carries an audio track (<c>Audio:Enabled</c>).</summary>
    bool CarriesAudio { get; }

    /// <summary>
    /// Sends one Opus packet on the audio track, at once (audio never waits behind video).
    /// <paramref name="rtpTimestamp"/> is at 48 kHz; <paramref name="marker"/> starts a talkspurt.
    /// </summary>
    void SendAudio(byte[] opus, uint rtpTimestamp, bool marker);
}

/// <summary>
/// Since the last call: how long frames waited in the pacer (handed over → last packet sent),
/// how many packets the glasses NACKed, how many of those were sent again, and how many RTCP
/// packets from the glasses SIPSorcery couldn't decrypt (their reports, NACKs and PLIs unheard).
/// </summary>
public readonly record struct SendStats(double AvgMs, double MaxMs, int Nacked, int Resent, int RtcpUnreadable = 0);

public interface IMediaPeerFactory
{
    IMediaPeer Create(string codec);
}

public interface IFrameEncoderFactory
{
    IFrameEncoder Create();
}
