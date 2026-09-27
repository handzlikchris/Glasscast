using Concentus;
using Concentus.Enums;
using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Media;

/// <summary>
/// 48 kHz stereo Opus (Concentus, which SIPSorcery already depends on), set up for a remote
/// desktop's sound over a lossy link:
/// - general audio, not speech-only (VoIP mode), since it's whatever the PC plays;
/// - in-band FEC: each packet carries a coarse copy of the one before, so a single loss is
///   repaired at the glasses without a resend; <see cref="PacketLossPercent"/> tunes how much;
/// - silence costs next to nothing: Opus's own DTX (≤ 2-byte packets, <see cref="IsDtx"/>) only
///   works in its speech modes, and this is general audio (CELT), where digital silence still
///   makes a 3-byte packet 50 times a second. So <see cref="AudioPump"/> sends DTX itself on
///   <see cref="IsDigitalSilence"/>: a header-only packet now and then, as WebRTC does.
/// Not thread-safe: one pump thread.
/// </summary>
public sealed class OpusAudioEncoder
{
    /// <summary>The largest packet Opus can make (RFC 6716), which also bounds ours.</summary>
    private const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;
    private readonly byte[] _packet = new byte[MaxPacketBytes];

    public OpusAudioEncoder(int kbps, int frameMs)
    {
        FrameMs = frameMs is 10 or 20 or 40 or 60 ? frameMs : 20;
        _encoder = OpusCodecFactory.CreateEncoder(AudioFormat48k.SampleRate, AudioFormat48k.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = Math.Clamp(kbps, 12, 256) * 1000;
        _encoder.UseInbandFEC = true;
        _encoder.UseDTX = true;
        _encoder.UseVBR = true;
        // Middle of the range: Concentus is managed code, and at 10 it costs several times more CPU
        // for a difference nobody hears through the glasses' speakers.
        _encoder.Complexity = 5;
        _encoder.PacketLossPercent = 0;
    }

    public int FrameMs { get; }

    /// <summary>Samples per channel in one packet (960 at 20 ms).</summary>
    public int FrameSamples => AudioFormat48k.SampleRate / 1000 * FrameMs;

    /// <summary>The glasses' recent loss, 0-100: more of each packet goes to FEC as it rises.</summary>
    public int PacketLossPercent
    {
        get => _encoder.PacketLossPercent;
        set => _encoder.PacketLossPercent = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// Encodes one frame (<see cref="FrameSamples"/> × 2 interleaved floats, -1..1). The result
    /// is valid until the next call.
    /// </summary>
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> interleaved)
    {
        var length = _encoder.Encode(interleaved, FrameSamples, _packet, _packet.Length);
        return _packet.AsSpan(0, length);
    }

    /// <summary>
    /// A packet this short only says "silent" (DTX): no sound in it. The glasses' jitter buffer
    /// takes one as the start of silence and plays silence until sound comes again.
    /// </summary>
    public static bool IsDtx(ReadOnlySpan<byte> packet) => packet.Length <= 2;

    /// <summary>Nothing a 16-bit output could play: what loopback delivers while nothing plays.</summary>
    public static bool IsDigitalSilence(ReadOnlySpan<float> samples)
    {
        const float lsb = 1f / 32768;
        foreach (var s in samples)
        {
            if (s is > lsb or < -lsb)
            {
                return false;
            }
        }
        return true;
    }
}
