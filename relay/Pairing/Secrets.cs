using System.Security.Cryptography;

namespace GlassesRemote.Server.Pairing;

/// <summary>Random identifiers, pairing codes and session tokens.</summary>
public static class Secrets
{
    // No 0/O, 1/I/L: the code is read off the glasses and compared by eye.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int TokenBytes = 32;

    /// <summary>6-character human-readable code, shown as "ABC-123". Display only, never a credential.</summary>
    public static string NewPairingCode()
    {
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }
        return $"{chars[..3]}-{chars[3..]}";
    }

    /// <summary>
    /// A connect code: the glasses show it, you type it into the phone's companion app, and the
    /// relay joins them. Same look as a pairing code; single use, and only while the glasses wait.
    /// </summary>
    public static string NewConnectCode() => NewPairingCode();

    /// <summary>Whether <paramref name="code"/> has the shape of a code ("ABC-123", from the alphabet).</summary>
    public static bool IsCode(string code)
    {
        if (code.Length != 7 || code[3] != '-')
        {
            return false;
        }

        for (var i = 0; i < code.Length; i++)
        {
            if (i != 3 && CodeAlphabet.IndexOf(code[i]) < 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>128-bit opaque id for internal bookkeeping.</summary>
    public static string NewId() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>256-bit session token, base64url. Only its hash is kept server-side.</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    /// <summary>Constant-time comparison of a presented token against a stored hash.</summary>
    public static bool TokenMatches(string presented, byte[] storedHash)
    {
        // Reject absurd input before hashing; a real token is 43 characters.
        if (presented.Length is 0 or > 128)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(HashToken(presented), storedHash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
