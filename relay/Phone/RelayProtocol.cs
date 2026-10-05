using System.Text.Json;
using GlassesRemote.Server.Protocol;

namespace GlassesRemote.Server.Phone;

/// <summary>
/// A validated message from the glasses in a phone relay. The PC passes these on to the phone's
/// companion without acting on them: pairing and authentication are between the glasses and the
/// phone (architecture/phone-mode.md, "Pairing and authentication"), and the PC holds no secret.
/// </summary>
public abstract record GlassesSignal;

/// <summary>Pairing: a commitment to the glasses' key (SHA-256), before they see the phone's.</summary>
public sealed record PairStartSignal(string Commit) : GlassesSignal;

/// <summary>Pairing: the glasses' public key, once they have the phone's.</summary>
public sealed record PairRevealSignal(string Key) : GlassesSignal;

/// <summary>A paired device starts a session: its pairing id and a fresh nonce.</summary>
public sealed record HelloSignal(string Id, string Nonce) : GlassesSignal;

/// <summary>The glasses' proof that they hold the pairing key.</summary>
public sealed record ProofSignal(string Mac) : GlassesSignal;

public sealed record AnswerSignal(string Sdp, string Mac) : GlassesSignal;

public sealed record GlassesIceSignal(IceCandidateMessage Candidate) : GlassesSignal;

/// <summary>
/// First message of a phone relay: which phone (its id from an earlier connection), or none yet.
/// Without a known id the glasses get a connect code to type into the companion.
/// </summary>
public sealed record ConnectSignal(string? Phone) : GlassesSignal;

/// <summary>Answered by the PC itself (the relay's round trip); never passed on.</summary>
public sealed record RelayPingSignal(double T) : GlassesSignal;

/// <summary>
/// Strict parser for the glasses' side of a phone relay, in the style of <c>ControlProtocol</c>.
/// Keys, nonces and MACs are base64url of fixed sizes; anything else is rejected, and there is no
/// input: the glasses' input for the phone only ever goes over WebRTC.
/// </summary>
public static class RelayProtocol
{
    /// <summary>base64url (no padding) lengths: SHA-256 / HMAC-SHA256, a P-256 public point, 16 bytes, 12 bytes.</summary>
    public const int HashLength = 43;
    public const int PublicKeyLength = 87;
    public const int NonceLength = 22;
    public const int IdLength = 16;

    /// <summary>A phone's id on the server: 128 bits, base64url.</summary>
    public const int PhoneIdLength = 22;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4 };

    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out GlassesSignal? message, out string? error)
    {
        message = null;
        error = null;
        if (utf8Json.Length == 0 || utf8Json.Length > JsonRules.MaxMessageBytes)
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
                "pairStart" => JsonRules.Only(e, "commit") && B64u(e, "commit", HashLength, out var commit)
                    ? new PairStartSignal(commit)
                    : null,
                "pairReveal" => JsonRules.Only(e, "key") && B64u(e, "key", PublicKeyLength, out var key)
                    ? new PairRevealSignal(key)
                    : null,
                "hello" => JsonRules.Only(e, "id", "nonce") && B64u(e, "id", IdLength, out var id)
                           && B64u(e, "nonce", NonceLength, out var nonce)
                    ? new HelloSignal(id, nonce)
                    : null,
                "proof" => JsonRules.Only(e, "mac") && B64u(e, "mac", HashLength, out var mac)
                    ? new ProofSignal(mac)
                    : null,
                "rtcAnswer" => JsonRules.Only(e, "sdp", "mac")
                               && JsonRules.Str(e, "sdp", JsonRules.MaxSdpLength, out var sdp)
                               && B64u(e, "mac", HashLength, out var answerMac)
                    ? new AnswerSignal(sdp, answerMac)
                    : null,
                "iceCandidate" => JsonRules.ParseIce(e) is { } candidate ? new GlassesIceSignal(candidate) : null,
                "ping" => JsonRules.Only(e, "t") && JsonRules.Num(e, "t", out var t) ? new RelayPingSignal(t) : null,
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

    public static bool TryParse(string json, out GlassesSignal? message, out string? error) =>
        TryParse(System.Text.Encoding.UTF8.GetBytes(json), out message, out error);

    /// <summary>The first message on a relay-only server's session socket: <c>{type:"phone", phone?}</c>, or null.</summary>
    public static ConnectSignal? TryParseConnect(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > JsonRules.MaxMessageBytes)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray(), DocumentOptions);
            var e = doc.RootElement;
            return e.ValueKind == JsonValueKind.Object
                   && e.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                   && type.GetString() == "phone"
                ? ParseConnect(e)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first message of a phone relay, <c>{type:"phone", phone?}</c>, from an already parsed
    /// object (the PC's session socket parses its first message with <c>ControlProtocol</c>).
    /// </summary>
    public static ConnectSignal? ParseConnect(JsonElement e)
    {
        if (!JsonRules.Only(e, "phone"))
        {
            return null;
        }

        if (!e.TryGetProperty("phone", out _))
        {
            return new ConnectSignal(null);
        }

        return B64u(e, "phone", PhoneIdLength, out var phone) ? new ConnectSignal(phone) : null;
    }

    /// <summary>A base64url string (no padding) of exactly <paramref name="length"/> characters.</summary>
    internal static bool B64u(JsonElement e, string name, int length, out string value)
    {
        if (!JsonRules.Str(e, name, length, out value) || value.Length != length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }
        }
        return true;
    }
}
