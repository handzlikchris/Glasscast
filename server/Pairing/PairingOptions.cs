namespace GlassesRemote.Server.Pairing;

public sealed class PairingOptions
{
    public const string SectionName = "Pairing";

    /// <summary>How long a pairing popup waits for Approve/Reject before rejecting itself.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long an approved token stays usable before the glasses must present it.</summary>
    public TimeSpan TokenUseWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Pairing requests allowed per source IP within <see cref="RateWindow"/>.</summary>
    public int MaxRequestsPerIp { get; set; } = 5;

    /// <summary>Pairing requests allowed from all sources within <see cref="RateWindow"/>.</summary>
    public int MaxRequestsGlobal { get; set; } = 20;

    public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long approved glasses can reconnect without a new approval, counted from the approval
    /// and never extended. Zero turns remembering off: every session needs the popup.
    /// </summary>
    public TimeSpan DeviceGrantLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Where the remembered device's hashes are kept across restarts; null = memory only.</summary>
    public string? DeviceGrantFile { get; set; }

    /// <summary>How long a reconnecting device waits for its own stale session to close.</summary>
    public TimeSpan TakeoverTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public static string DefaultDeviceGrantFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassesRemote", "device-grant.json");
}
