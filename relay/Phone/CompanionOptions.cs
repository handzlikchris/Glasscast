namespace GlassesRemote.Server.Phone;

/// <summary>How a phone's companion app gets to use this server.</summary>
public enum CompanionRegistration
{
    /// <summary>Someone clicks Approve in a popup on the PC (the Windows server).</summary>
    Approve,

    /// <summary>
    /// Any companion may register, rate-limited (a hosted relay, with no one to click). Safe because
    /// the phone is the gate: it pairs the glasses itself (the same code on both, Approve on the
    /// phone), checks them every session, and asks for Android's capture consent every time.
    /// </summary>
    Open,
}

/// <summary><c>Companion:*</c> settings: the phone companion app's pairing and connection.</summary>
public sealed class CompanionOptions
{
    public const string SectionName = "Companion";

    public CompanionRegistration Registration { get; set; } = CompanionRegistration.Approve;

    /// <summary>Most phones kept at once; when full, phones unseen for <see cref="ForgetAfter"/> make room.</summary>
    public int MaxPhones { get; set; } = 1000;

    /// <summary>With open registration, a phone that hasn't connected for this long may be dropped to make room.</summary>
    public TimeSpan ForgetAfter { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Connect codes a phone may try within <see cref="RateWindow"/> (a wrong one counts).</summary>
    public int MaxClaimsPerPhone { get; set; } = 5;

    /// <summary>How long the Approve popup for a phone waits before rejecting itself.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Registrations (Approve popups, or open registrations) allowed per source IP within <see cref="RateWindow"/>.</summary>
    public int MaxRequestsPerIp { get; set; } = 5;

    /// <summary>Registrations allowed from all sources within <see cref="RateWindow"/>.</summary>
    public int MaxRequestsGlobal { get; set; } = 20;

    public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The companion's first message (pair or auth) must arrive within this.</summary>
    public TimeSpan AuthTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A connected companion that sends nothing (it pings every 15 s) for this long is dropped.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long glasses wait in a phone relay to find their phone: for it to connect, or for their
    /// connect code to be typed into a companion (on the phone, not in a hurry).
    /// </summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A phone relay's life once the phone is found: pairing on the phone, the capture consent, the
    /// offer and answer. The glasses close it once their video is up; the session itself doesn't
    /// need the server.
    /// </summary>
    public TimeSpan RelayTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Phone relays glasses may open per source IP within <see cref="RateWindow"/>.</summary>
    public int MaxRelaysPerIp { get; set; } = 10;

    /// <summary>Phone relays allowed from all sources within <see cref="RateWindow"/>.</summary>
    public int MaxRelaysGlobal { get; set; } = 30;

    public int MaxMessagesPerSecond { get; set; } = 60;

    /// <summary>Sustained message rate allowed from glasses in a phone relay; bursts up to twice this.</summary>
    public int RelayMaxMessagesPerSecond { get; set; } = 120;

    /// <summary>Where the registered phones (id, name, token hash, dates) are kept; null = the default under LocalAppData.</summary>
    public string? PhonesFile { get; set; }

    /// <summary>
    /// The single paired phone of earlier versions; imported into <see cref="PhonesFile"/> once, when
    /// that doesn't exist yet. Null = the default under LocalAppData.
    /// </summary>
    public string? GrantFile { get; set; }

    public static string DefaultPhonesFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassesRemote", "phones.json");

    public static string DefaultGrantFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassesRemote", "companion-grant.json");
}
