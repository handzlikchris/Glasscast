using System.Text.Json;
using GlassesRemote.Server.Protocol;

namespace GlassesRemote.Server.Phone;

/// <summary>Where the phone is in a session, as the companion reports it (and the glasses are told).</summary>
public enum PhoneState
{
    /// <summary>The phone shows Android's screen-capture consent dialog.</summary>
    Asking,

    /// <summary>Someone tapped Cancel on the phone.</summary>
    Declined,

    /// <summary>Capturing; the offer follows (or has gone).</summary>
    Live,

    /// <summary>The phone stopped before the video was up: its Stop button, the screen locked, capture ended.</summary>
    Ended,
}

/// <summary>A validated message from the companion app. Anything else is rejected before it gets here.</summary>
public abstract record CompanionMessage;

/// <summary>First message of an unpaired companion: ask for the Approve popup on the PC.</summary>
public sealed record CompanionPairMessage(string Name) : CompanionMessage;

/// <summary>First message of a paired companion.</summary>
public sealed record CompanionAuthMessage(string Token) : CompanionMessage
{
    // Never print the token, even in debug output.
    public override string ToString() => "CompanionAuthMessage { Token = *** }";
}

public sealed record CompanionPingMessage(double T) : CompanionMessage;

public sealed record CompanionStateMessage(PhoneState State) : CompanionMessage;

/// <summary>The phone's offer, with its MAC under the session key the glasses and phone agreed.</summary>
public sealed record CompanionOfferMessage(string Sdp, string Mac) : CompanionMessage;

public sealed record CompanionIceMessage(IceCandidateMessage Candidate) : CompanionMessage;

// The phone's side of pairing and authenticating the glasses (the PC only passes these on).

/// <summary>Pairing: the phone's public key.</summary>
public sealed record CompanionPairKeyMessage(string Key) : CompanionMessage;

/// <summary>Pairing: approved on the phone.</summary>
public sealed record CompanionPairedMessage : CompanionMessage;

/// <summary>Pairing: rejected on the phone, timed out, or the glasses' key didn't match their commitment.</summary>
public sealed record CompanionPairFailedMessage : CompanionMessage;

/// <summary>The phone's nonce and its proof that it holds the pairing key.</summary>
public sealed record CompanionChallengeMessage(string Nonce, string Mac) : CompanionMessage;

/// <summary>The phone doesn't know these glasses, or their proof was wrong.</summary>
public sealed record CompanionAuthFailedMessage : CompanionMessage;

/// <summary>
/// Strict parser for the companion socket, in the same style as <see cref="ControlProtocol"/>:
/// a small JSON object, a known type, exactly its properties, capped sizes. The PC only relays
/// signalling, pairing and authentication between the phone and the glasses, so that's all there is.
/// </summary>
public static class CompanionProtocol
{
    public const int MaxMessageBytes = ControlProtocol.MaxMessageBytes;
    public const int MaxNameLength = 32;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4 };

    private static readonly Dictionary<string, PhoneState> States = new(StringComparer.Ordinal)
    {
        ["asking"] = PhoneState.Asking,
        ["declined"] = PhoneState.Declined,
        ["live"] = PhoneState.Live,
        ["ended"] = PhoneState.Ended,
    };

    /// <summary>The wire name of a state, as the glasses get it in phoneStatus.</summary>
    public static string StateName(PhoneState state) => States.First(pair => pair.Value == state).Key;

    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out CompanionMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (utf8Json.Length == 0 || utf8Json.Length > MaxMessageBytes)
        {
            error = "size";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                error = "shape";
                return false;
            }

            message = typeElement.GetString() switch
            {
                "pair" => ParsePair(e),
                "auth" => ControlProtocol.Only(e, "token") && ControlProtocol.Str(e, "token", ControlProtocol.MaxTokenLength, out var token)
                    ? new CompanionAuthMessage(token)
                    : null,
                "ping" => ControlProtocol.Only(e, "t") && ControlProtocol.Num(e, "t", out var t) ? new CompanionPingMessage(t) : null,
                "sessionState" => ControlProtocol.Only(e, "state") && ControlProtocol.Str(e, "state", 16, out var state)
                                  && States.TryGetValue(state, out var parsed)
                    ? new CompanionStateMessage(parsed)
                    : null,
                "rtcOffer" => ControlProtocol.Only(e, "sdp", "mac") && ControlProtocol.Str(e, "sdp", ControlProtocol.MaxSdpLength, out var sdp)
                               && RelayProtocol.B64u(e, "mac", RelayProtocol.HashLength, out var offerMac)
                    ? new CompanionOfferMessage(sdp, offerMac)
                    : null,
                "pairKey" => ControlProtocol.Only(e, "key") && RelayProtocol.B64u(e, "key", RelayProtocol.PublicKeyLength, out var key)
                    ? new CompanionPairKeyMessage(key)
                    : null,
                "paired" => ControlProtocol.Only(e) ? new CompanionPairedMessage() : null,
                "pairFailed" => ControlProtocol.Only(e) ? new CompanionPairFailedMessage() : null,
                "challenge" => ControlProtocol.Only(e, "nonce", "mac")
                               && RelayProtocol.B64u(e, "nonce", RelayProtocol.NonceLength, out var nonce)
                               && RelayProtocol.B64u(e, "mac", RelayProtocol.HashLength, out var mac)
                    ? new CompanionChallengeMessage(nonce, mac)
                    : null,
                "authFailed" => ControlProtocol.Only(e) ? new CompanionAuthFailedMessage() : null,
                "iceCandidate" => ControlProtocol.ParseIce(e) is { } candidate ? new CompanionIceMessage(candidate) : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            error = "json";
            return false;
        }

        if (message is null)
        {
            error = "invalid";
            return false;
        }

        return true;
    }

    public static bool TryParse(string json, out CompanionMessage? message, out string? error) =>
        TryParse(System.Text.Encoding.UTF8.GetBytes(json), out message, out error);

    private static CompanionMessage? ParsePair(JsonElement e)
    {
        if (!ControlProtocol.Only(e, "name") || !ControlProtocol.Str(e, "name", MaxNameLength, out var name))
        {
            return null;
        }

        // Shown in the Approve popup: printable text on one line only.
        var flattened = ControlProtocol.FlattenText(name);
        return flattened.Length == 0 ? null : new CompanionPairMessage(flattened);
    }
}
