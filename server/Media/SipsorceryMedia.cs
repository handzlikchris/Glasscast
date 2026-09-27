using System.Net;
using GlassesRemote.Server.Hosting;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace GlassesRemote.Server.Media;

/// <summary>VP8 via libvpx (bundled with SIPSorceryMedia.Encoders).</summary>
public sealed class Vp8FrameEncoder : IFrameEncoder
{
    private readonly VpxVideoEncoder _encoder;

    public Vp8FrameEncoder(int targetKbps)
    {
        _encoder = new VpxVideoEncoder { TargetKbps = (uint)Math.Max(100, targetKbps) };
    }

    public string Codec => "VP8";

    public byte[]? Encode(byte[] bgra, int width, int height) =>
        _encoder.EncodeVideo(width, height, bgra, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);

    public void ForceKeyFrame() => _encoder.ForceKeyFrame();

    public void Dispose() => _encoder.Dispose();
}

/// <summary>Picks the configured codec; H.264 falls back to VP8 where Windows has no H.264 encoder.</summary>
public sealed class FrameEncoderFactory(IOptions<MediaOptions> options, ILogger<FrameEncoderFactory> logger) : IFrameEncoderFactory
{
    private readonly Lazy<bool> _h264Available = new(MfH264Encoder.IsAvailable);

    public IFrameEncoder Create()
    {
        var media = options.Value;
        if (string.Equals(media.Codec, "H264", StringComparison.OrdinalIgnoreCase))
        {
            if (_h264Available.Value)
            {
                return new MfH264Encoder(media.TargetKbps, media.FramesPerSecond);
            }
            logger.LogWarning("No H.264 encoder on this PC (Windows N edition?); falling back to VP8");
        }
        return new Vp8FrameEncoder(media.TargetKbps);
    }
}

/// <summary>
/// Send-only video peer on the fixed media port. The offer advertises the router's
/// public IP as a host candidate, so the glasses connect straight through the port
/// forward; SIPSorcery learns their address from the incoming checks.
/// </summary>
public sealed class SipsorceryMediaPeer : IMediaPeer
{
    private readonly RTCPeerConnection _peer;
    private readonly MediaOptions _options;
    private readonly ILogger _logger;
    private readonly bool _h264;
    private readonly RtpPacer _pacer;
    private uint? _rtpTimestamp;
    private int _payloadType = -1;
    private int _closed;
    private int _compoundRequests;
    private int _standaloneRequests;

    /// <summary>
    /// An RTCP packet whose first part is a payload-specific feedback (PT 206) PLI (FMT 1) or FIR
    /// (FMT 4). Only the unencrypted first 8 bytes of SRTCP are read.
    /// </summary>
    internal static bool IsStandaloneKeyframeRequest(byte[] packet) =>
        packet.Length >= 12
        && packet[0] is >= 128 and <= 191   // RTP/RTCP version 2
        && packet[1] == 206                 // PSFB
        && (packet[0] & 0x1F) is 1 or 4;    // PLI or FIR

    private void OnKeyframeRequest(string how, ref int count)
    {
        // Log the first few of each shape: which one the glasses use is worth knowing.
        if (Interlocked.Increment(ref count) <= 3)
        {
            _logger.LogInformation("Keyframe request from the glasses, {How}", how);
        }
        KeyframeRequested?.Invoke();
    }


    public SipsorceryMediaPeer(MediaOptions options, string codec, ILogger logger)
    {
        _options = options;
        _logger = logger;
        var config = new RTCConfiguration();
        if (IPAddress.TryParse(options.BindAddress, out var bindAddress))
        {
            // Pin the socket to one adapter so replies leave the way requests came in.
            config.X_BindAddress = bindAddress;
        }
        _peer = new RTCPeerConnection(config, bindPort: options.MediaPort);

        _h264 = codec == "H264";
        var format = _h264
            ? new VideoFormat(VideoCodecsEnum.H264, 102, 90000, "packetization-mode=1;profile-level-id=42e01f")
            : new VideoFormat(VideoCodecsEnum.VP8, 96);
        _peer.addTrack(new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly));
        _pacer = new RtpPacer(SendPacket, options.PacingKbps, TimeSpan.FromMilliseconds(options.MaxPacingDelayMs), logger);

        // Lost packets aren't resent, so a keyframe is the only way the glasses recover a broken
        // picture; they ask with PLI (or FIR) and we answer on the next frame. The request arrives
        // in one of two shapes:
        // - inside a compound report (receiver report first): SIPSorcery decrypts and parses it and
        //   raises OnReceiveReport. It keeps only the report's last feedback item, so a PLI followed
        //   by a REMB is lost; with rtcp-rsize offered (SdpFeedback) Chrome no longer sends PLIs this way;
        // - on its own (PLI first): SIPSorcery matches RTCP to a stream by the sender's SSRC, which
        //   for a receive-only browser is none of ours, so it drops the packet unseen. The first
        //   RTCP header isn't encrypted (SRTCP), so it's read here straight off the channel.
        _peer.OnReceiveReport += (_, media, report) =>
        {
            if (media == SDPMediaTypesEnum.video
                && (report.ReceiverReport is not null || report.SenderReport is not null)
                && report.Feedback?.Header is { } header
                && header.PacketType == RTCPReportTypesEnum.PSFB
                && header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
            {
                OnKeyframeRequest("in a compound report", ref _compoundRequests);
            }
        };
        _peer.GetRtpChannel().OnRTPDataReceived += (_, _, packet) =>
        {
            if (IsStandaloneKeyframeRequest(packet))
            {
                OnKeyframeRequest("on its own", ref _standaloneRequests);
            }
        };

        _peer.onconnectionstatechange += state =>
        {
            _logger.LogInformation("Media peer state: {State}", state);
            if (state == RTCPeerConnectionState.connected)
            {
                Connected?.Invoke();
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                RaiseClosed();
            }
        };
    }

    public event Action? Connected;

    public event Action? Closed;

    public event Action? KeyframeRequested;

    public bool IsConnected => _peer.connectionState == RTCPeerConnectionState.connected;

    public async Task<string> CreateOfferAsync()
    {
        var offer = _peer.createOffer();
        await _peer.setLocalDescription(offer);
        var publicIp = IPAddress.TryParse(_options.PublicIp, out var ip) ? ip : null;
        if (publicIp is null && !_options.IncludeLanCandidates)
        {
            _logger.LogWarning("No Media:PublicIp configured and LAN candidates are off: the glasses have no address to reach");
        }
        return SdpFeedback.AddKeyframeRequests(
            SdpCandidates.Rewrite(offer.sdp, publicIp, _options.MediaPort, _options.IncludeLanCandidates));
    }

    public bool ApplyAnswer(string sdp)
    {
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
        {
            _logger.LogWarning("Answer rejected: {Result}", result);
        }
        return result == SetDescriptionResultEnum.OK;
    }

    public void AddRemoteCandidate(string candidate, string? sdpMid, int sdpMLineIndex)
    {
        try
        {
            _peer.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = sdpMid,
                sdpMLineIndex = (ushort)sdpMLineIndex,
            });
        }
        catch (Exception ex)
        {
            // Browsers send mDNS (.local) host candidates we can't resolve; the
            // connection still forms from their checks (peer-reflexive).
            _logger.LogDebug(ex, "Ignored remote candidate");
        }
    }

    public uint SendFrame(byte[] encoded, uint durationRtpUnits)
    {
        if (!_h264)
        {
            // VP8 (fallback only) goes through SIPSorcery unpaced. SendVideo stamps the frame with
            // the track's current timestamp, then advances it.
            var vp8Timestamp = _peer.VideoLocalTrack?.Timestamp ?? 0;
            if (_framesToDrop > 0 && Interlocked.Decrement(ref _framesToDrop) >= 0)
            {
                return vp8Timestamp; // dev/test only, see DropFirstFrames
            }
            _peer.SendVideo(durationRtpUnits, encoded);
            return vp8Timestamp;
        }

        // H.264 is packetized here and paced (RtpPacer). The RTP clock starts where SIPSorcery's
        // track would have (a random value) and advances by each frame's duration.
        var rtpTimestamp = _rtpTimestamp ??= _peer.VideoLocalTrack?.Timestamp ?? 0;
        _rtpTimestamp = rtpTimestamp + durationRtpUnits;
        if (_framesToDrop > 0 && Interlocked.Decrement(ref _framesToDrop) >= 0)
        {
            return rtpTimestamp; // dev/test only, see DropFirstFrames
        }
        _pacer.Enqueue(H264Rtp.Packetize(encoded, rtpTimestamp));
        return rtpTimestamp;
    }

    public SendDelay TakeSendDelay() => _pacer.TakeSendDelay();

    /// <summary>On the pacer's thread: one packet out through SRTP, like SIPSorcery's SendVideo.</summary>
    private void SendPacket(RtpPacket packet)
    {
        var stream = _peer.VideoStream;
        if (stream is null || Volatile.Read(ref _closed) == 1)
        {
            return;
        }
        if (_payloadType < 0)
        {
            // The payload type the glasses accepted for H.264 in their answer.
            _payloadType = stream.GetSendingFormat().ID;
        }
        stream.SetRtpHeaderExtensionValue(TransportWideCCExtension.RTP_HEADER_EXTENSION_URI, null);
        stream.SendRtpRaw(packet.Payload, packet.Timestamp, packet.Marker ? 1 : 0, _payloadType);
    }

    private int _framesToDrop;

    /// <summary>
    /// DEV/TEST ONLY (e2e harness): doesn't send the next <paramref name="count"/> frames, like a
    /// session whose first keyframe was lost. The glasses then get frames they can't decode and
    /// ask for a keyframe.
    /// </summary>
    internal void DropFirstFrames(int count) => _framesToDrop = count;

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Closed?.Invoke();
        }
    }

    public void Dispose()
    {
        _pacer.Dispose();
        _peer.close();
        RaiseClosed();
    }
}

public sealed class MediaPeerFactory(IOptions<MediaOptions> options, ILoggerFactory loggers) : IMediaPeerFactory
{
    public IMediaPeer Create(string codec) =>
        new SipsorceryMediaPeer(options.Value, codec, loggers.CreateLogger<SipsorceryMediaPeer>());
}
