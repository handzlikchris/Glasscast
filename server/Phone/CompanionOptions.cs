namespace GlassesRemote.Server.Phone;

/// <summary><c>Companion:*</c> settings: the phone companion app's pairing and connection.</summary>
public sealed class CompanionOptions
{
    public const string SectionName = "Companion";

    /// <summary>How long the Approve popup for a phone waits before rejecting itself.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Pairing requests allowed per source IP within <see cref="RateWindow"/>.</summary>
    public int MaxRequestsPerIp { get; set; } = 5;

    /// <summary>Pairing requests allowed from all sources within <see cref="RateWindow"/>.</summary>
    public int MaxRequestsGlobal { get; set; } = 20;

    public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The companion's first message (pair or auth) must arrive within this.</summary>
    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A connected companion that sends nothing (it pings every 15 s) for this long is dropped.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// From a phone session's start to the phone's offer: waiting for the companion to connect and
    /// for someone to tap the capture consent on the phone. Then the session gives up.
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public int MaxMessagesPerSecond { get; set; } = 60;

    /// <summary>Where the paired phone's token hash is kept; null = the default under LocalAppData.</summary>
    public string? GrantFile { get; set; }

    public static string DefaultGrantFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassesRemote", "companion-grant.json");
}
