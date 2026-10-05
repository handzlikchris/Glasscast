using System.Text;
using System.Text.Json;

namespace GlassesRemote.Server.Protocol;

/// <summary>
/// The strict-parsing rules every socket shares (<c>ControlProtocol</c> on the PC, and the phone
/// relay's <see cref="GlassesRemote.Server.Phone.RelayProtocol"/> and
/// <see cref="GlassesRemote.Server.Phone.CompanionProtocol"/>): exactly the allowed properties,
/// values of the right kind and size, text flattened so it can never press Enter.
/// </summary>
public static class JsonRules
{
    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxSdpLength = 12 * 1024;
    public const int MaxCandidateLength = 1024;
    public const int MaxTokenLength = 128;

    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>An ICE candidate's fields ("candidate", "sdpMid", "sdpMLineIndex"); shared by the glasses' and the companion's parsers.</summary>
    public static IceCandidateMessage? ParseIce(JsonElement e)
    {
        if (!Only(e, "candidate", "sdpMid", "sdpMLineIndex") || !Str(e, "candidate", MaxCandidateLength, out var candidate))
        {
            return null;
        }

        string? mid = null;
        if (e.TryGetProperty("sdpMid", out var midElement) && midElement.ValueKind != JsonValueKind.Null)
        {
            if (!Str(e, "sdpMid", 32, out var midValue))
            {
                return null;
            }
            mid = midValue;
        }

        var index = 0;
        if (e.TryGetProperty("sdpMLineIndex", out var indexElement) && indexElement.ValueKind != JsonValueKind.Null)
        {
            if (!Int(e, "sdpMLineIndex", out index) || index is < 0 or > 16)
            {
                return null;
            }
        }

        return new IceCandidateMessage(candidate, mid, index);
    }

    /// <summary>
    /// Line breaks and tabs become spaces so voice text can never press Enter or Tab;
    /// other control characters are dropped.
    /// </summary>
    public static string FlattenText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t' or LineSeparator or ParagraphSeparator)
            {
                sb.Append(' ');
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    public static bool Only(JsonElement e, params string[] allowed)
    {
        foreach (var property in e.EnumerateObject())
        {
            if (property.Name != "type" && Array.IndexOf(allowed, property.Name) < 0)
            {
                return false;
            }
        }
        return true;
    }

    public static bool Str(JsonElement e, string name, int maxLength, out string value)
    {
        value = "";
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = p.GetString()!;
        return value.Length <= maxLength;
    }

    public static bool Bool(JsonElement e, string name, out bool value)
    {
        value = false;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = p.GetBoolean();
        return true;
    }

    public static bool Num(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.Number
               && p.TryGetDouble(out value)
               && double.IsFinite(value);
    }

    public static bool Int(JsonElement e, string name, out int value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (p.TryGetInt32(out value))
        {
            return true;
        }

        // Out-of-range integers saturate instead of failing, so clamping still applies.
        if (p.TryGetDouble(out var d) && double.IsFinite(d) && Math.Floor(d) == d)
        {
            value = d > 0 ? int.MaxValue : int.MinValue;
            return true;
        }

        return false;
    }
}
