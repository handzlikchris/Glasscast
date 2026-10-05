namespace GlassesRemote.Server.Hosting;

public sealed class ControlSessionOptions
{
    public const string SectionName = "Session";

    /// <summary>Unauthenticated session sockets are closed after this long.</summary>
    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A session with no user input (pings don't count) for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// A session that hears nothing at all from the glasses for this long is closed: their page
    /// pings every 2 s and reports stats every second, so silence means it was closed, hidden or
    /// frozen, even while the browser keeps the socket and the video connection alive.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Hard cap on one session, whatever happens.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Sustained message rate allowed per session; bursts up to twice this.</summary>
    public int MaxMessagesPerSecond { get; set; } = 120;
}

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>Router's static public IP, advertised to the glasses as the media address.</summary>
    public string PublicIp { get; set; } = "";

    /// <summary>
    /// LAN address of the adapter the router forwards the media port to (e.g. 192.168.1.114).
    /// Needed when the PC has more than one network adapter: without it, replies can leave
    /// through a different adapter, the router rewrites them as a new connection, and the
    /// glasses discard them (video stuck on "connecting"). Empty = all adapters.
    /// </summary>
    public string BindAddress { get; set; } = "";

    /// <summary>Fixed UDP port for WebRTC media, forwarded on the router. Must be even (SIPSorcery requires it).</summary>
    public int MediaPort { get; set; } = 50000;

    /// <summary>Also advertise all of SIPSorcery's LAN addresses (for testing on the home network).</summary>
    public bool IncludeLanCandidates { get; set; }

    /// <summary>
    /// When the offer also carries <see cref="BindAddress"/>:<see cref="MediaPort"/>, ranked
    /// above the public address, so the video goes straight over the LAN when the glasses can
    /// reach it (and over the internet when they can't):
    /// <see cref="LanOffer.Always"/> (default): every session. The glasses reach the internet
    /// through the phone, which may use mobile data while sitting on the home Wi-Fi, so their
    /// public address doesn't say whether the LAN is reachable; trying costs nothing.
    /// <see cref="LanOffer.Home"/>: only sessions from <see cref="PublicIp"/> itself (behind
    /// this router), so the LAN address never goes to devices elsewhere.
    /// <see cref="LanOffer.Never"/>: internet only.
    /// </summary>
    public LanOffer OfferLan { get; set; } = LanOffer.Always;

    /// <summary>"H264" (Windows Media Foundation, falls back to VP8 if unavailable) or "VP8".</summary>
    public string Codec { get; set; } = "H264";

    public int FramesPerSecond { get; set; } = 20;

    /// <summary>The most the encoder is asked for; the target adapts below it (BitrateController).</summary>
    public int TargetKbps { get; set; } = 2500;

    /// <summary>The target never adapts below this.</summary>
    public int MinKbps { get; set; } = 300;

    /// <summary>
    /// Where the target starts; it climbs 8% a second while the link is clean and busy. The
    /// glasses' link measured ~0.9 Mbit/s with the phone on 5G, so starting at the max (2.5)
    /// would overload it first and back off after.
    /// </summary>
    public int StartKbps { get; set; } = 1000;

    /// <summary>
    /// DIAGNOSTIC: every session starts with a link test (LinkTest): a noise pattern at each of
    /// <see cref="LinkTestStepsKbps"/> for <see cref="LinkTestStepSeconds"/>, then the desktop.
    /// </summary>
    public bool LinkTestOnStart { get; set; }

    public int[] LinkTestStepsKbps { get; set; } = [500, 1000, 2000, 4000, 8000];

    public int LinkTestStepSeconds { get; set; } = 3;

    /// <summary>
    /// Periodic keyframes, a safety net: lost packets are resent on NACK, and a picture that still
    /// breaks gets a keyframe on request (PLI). Every 2 s they were the main source of loss over
    /// mobile data (40-90 KB each). Also the H.264 encoder's own keyframe interval (GOP).
    /// </summary>
    public int KeyframeIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Keyframes the glasses ask for (PLI/FIR) are sent at once, but no closer together than this:
    /// they repeat the request every ~200 ms until one arrives, and each keyframe is 40-90 KB. At
    /// 500 ms, on a struggling link, requested keyframes came 1-2 a second and made it worse.
    /// </summary>
    public int RequestedKeyframeMinGapMs { get; set; } = 1500;

    /// <summary>
    /// H.264 packets go out at this rate at least instead of a frame at a time (see RtpPacer):
    /// bursts of a keyframe's 35-75 packets get dropped on mobile links. 0 = no pacing.
    /// </summary>
    public int PacingKbps { get; set; } = 6000;

    /// <summary>Pacing speeds up so nothing waits longer than this to be sent.</summary>
    public int MaxPacingDelayMs { get; set; } = 150;

    public int FrameWidth { get; set; } = 600;

    public int FrameHeight { get; set; } = 600;
}

/// <summary>
/// The PC's sound on the glasses (Opus over the video's WebRTC connection). The glasses turn it
/// on and off (their ♪ button); this only says whether it's on offer and how it's encoded.
/// </summary>
public sealed class AudioOptions
{
    public const string SectionName = "Audio";

    /// <summary>Offer audio at all. Off: no audio track in the offer, no ♪ button on the glasses.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Opus bitrate, stereo. About 1.4x that on the wire with packet headers (50 packets a second);
    /// during silence Opus sends next to nothing (DTX).
    /// </summary>
    public int Kbps { get; set; } = 40;

    /// <summary>Audio per packet: 10, 20, 40 or 60 ms. Longer saves header bytes and adds that much delay.</summary>
    public int FrameMs { get; set; } = 20;
}

/// <summary>Which sessions are offered the PC's LAN address for the video (<see cref="MediaOptions.OfferLan"/>).</summary>
public enum LanOffer
{
    Always,
    Home,
    Never,
}
