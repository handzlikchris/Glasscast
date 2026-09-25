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
    private int _closed;

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

        var format = codec == "H264"
            ? new VideoFormat(VideoCodecsEnum.H264, 102, 90000, "packetization-mode=1;profile-level-id=42e01f")
            : new VideoFormat(VideoCodecsEnum.VP8, 96);
        _peer.addTrack(new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly));

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
        return SdpCandidates.Rewrite(offer.sdp, publicIp, _options.MediaPort, _options.IncludeLanCandidates);
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
        // SendVideo stamps the frame with the track's current timestamp, then advances it.
        var rtpTimestamp = _peer.VideoLocalTrack?.Timestamp ?? 0;
        _peer.SendVideo(durationRtpUnits, encoded);
        return rtpTimestamp;
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Closed?.Invoke();
        }
    }

    public void Dispose()
    {
        _peer.close();
        RaiseClosed();
    }
}

public sealed class MediaPeerFactory(IOptions<MediaOptions> options, ILoggerFactory loggers) : IMediaPeerFactory
{
    public IMediaPeer Create(string codec) =>
        new SipsorceryMediaPeer(options.Value, codec, loggers.CreateLogger<SipsorceryMediaPeer>());
}
