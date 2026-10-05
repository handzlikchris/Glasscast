namespace GlassesRemote.Server.Hosting;

public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>Exact origins allowed to open the WebSockets (scheme://host[:port]).</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// The public host name the glasses load the app from (e.g. glasses.example.com), behind Caddy.
    /// Adds https://{PublicHost} to <see cref="AllowedOrigins"/> and the host to AllowedHosts, so a
    /// deployment sets its name in one place (appsettings.Local.json or Web__PublicHost).
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Development only: also accept a WebSocket whose Origin is the site that served it
    /// (same scheme, host and port as the request). Used by the "lan" launch profile,
    /// where the PC's LAN address isn't known in advance. Keep off in production.
    /// </summary>
    public bool AllowSameOrigin { get; set; }

    /// <summary>Folder with the built glasses client (client-web/dist), relative to the content root.</summary>
    public string ClientRoot { get; set; } = "../client-web/dist";
}
