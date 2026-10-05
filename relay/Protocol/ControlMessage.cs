namespace GlassesRemote.Server.Protocol;

/// <summary>A validated message from the glasses. Anything else is rejected before it gets here.</summary>
public abstract record ControlMessage;

/// <summary>A trickled ICE candidate, from the glasses (PC or phone) or the phone.</summary>
public sealed record IceCandidateMessage(string Candidate, string? SdpMid, int SdpMLineIndex) : ControlMessage;
