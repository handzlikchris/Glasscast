using System.Net;

namespace GlassesRemote.Server.Pairing;

public enum PairingOutcomeKind
{
    Approved,
    Rejected,
    Expired,
    Cancelled,
}

public sealed record PairingOutcome(PairingOutcomeKind Kind, string? Token = null);

/// <summary>A pairing request waiting for Approve/Reject on the PC.</summary>
public sealed class PairingRequest
{
    internal PairingRequest(string id, string code, IPAddress remoteAddress, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        Code = code;
        RemoteAddress = remoteAddress;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string Id { get; }

    /// <summary>Shown on the glasses and in the popup, so you can tell your request from anyone else's.</summary>
    public string Code { get; }

    public IPAddress RemoteAddress { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    internal TaskCompletionSource<PairingOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<PairingOutcome> Outcome => Completion.Task;
}
