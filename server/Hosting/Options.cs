namespace GlassesRemote.Server.Hosting;

public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>Exact origins allowed to open the WebSockets (scheme://host[:port]).</summary>
    public string[] AllowedOrigins { get; set; } = ["https://glasses.example.com"];

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

    /// <summary>Fixed UDP port for WebRTC media, forwarded on the router.</summary>
    public int MediaPort { get; set; } = 50000;

    /// <summary>Also advertise LAN addresses (for testing on the home network).</summary>
    public bool IncludeLanCandidates { get; set; }

    /// <summary>"H264" (Windows Media Foundation, falls back to VP8 if unavailable) or "VP8".</summary>
    public string Codec { get; set; } = "H264";

    public int FramesPerSecond { get; set; } = 20;

    public int TargetKbps { get; set; } = 2500;

    /// <summary>Periodic keyframes let the stream recover quickly from packet loss.</summary>
    public int KeyframeIntervalSeconds { get; set; } = 2;

    public int FrameWidth { get; set; } = 600;

    public int FrameHeight { get; set; } = 600;
}
