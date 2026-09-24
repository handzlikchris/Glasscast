namespace WebRtcSpike;

public sealed class SpikeOptions
{
    public const string SectionName = "Spike";

    /// <summary>Router's public IP, advertised to the browser as an ICE candidate.</summary>
    public string PublicIp { get; set; } = "";

    /// <summary>Fixed UDP port the peer connection binds to (forwarded on the router).</summary>
    public int MediaPort { get; set; } = 50000;

    /// <summary>Also advertise the PC's LAN addresses (useful when testing on the LAN).</summary>
    public bool IncludeLanCandidates { get; set; } = true;

    public int FramesPerSecond { get; set; } = 30;

    public int TargetKbps { get; set; } = 1500;

    public int MaxSessionMinutes { get; set; } = 15;
}
