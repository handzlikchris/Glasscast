namespace GlassesRemote.Server.Hosting;

public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>Exact origins allowed to open the WebSockets (scheme://host[:port]).</summary>
    public string[] AllowedOrigins { get; set; } = ["https://glasses.example.com"];

    /// <summary>
    /// Development only: also accept a WebSocket whose Origin is the site that served it
    /// (same scheme, host and port as the request). Used by the "lan" launch profile,
    /// where the PC's LAN address isn't known in advance. Keep off in production.
    /// </summary>
    public bool AllowSameOrigin { get; set; }

    /// <summary>Folder with the built glasses client (client-web/dist), relative to the content root.</summary>
    public string ClientRoot { get; set; } = "../client-web/dist";
}

public sealed class ControlSessionOptions
{
    public const string SectionName = "Session";

    /// <summary>Unauthenticated session sockets are closed after this long.</summary>
    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A session with no user input (pings don't count) for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(60);

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

    /// <summary>Also advertise LAN addresses (for testing on the home network).</summary>
    public bool IncludeLanCandidates { get; set; }

    /// <summary>"H264" (Windows Media Foundation, falls back to VP8 if unavailable) or "VP8".</summary>
    public string Codec { get; set; } = "H264";

    public int FramesPerSecond { get; set; } = 20;

    /// <summary>The most the encoder is asked for; the target adapts below it (BitrateController).</summary>
    public int TargetKbps { get; set; } = 2500;

    /// <summary>The target never adapts below this.</summary>
    public int MinKbps { get; set; } = 300;

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
