namespace GlassesRemote.Server.Hosting;

public static class OriginPolicy
{
    /// <summary>Exact scheme://host[:port] match against the allowlist. A missing Origin is refused.</summary>
    /// <summary>Origin names exactly the scheme, host and port this request arrived on.</summary>
    public static bool IsSameOrigin(string? origin, HttpRequest request)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/")
        {
            return false;
        }

        var requestOrigin = $"{request.Scheme}://{request.Host.Value}";
        return Uri.TryCreate(requestOrigin, UriKind.Absolute, out var expected)
               && string.Equals(uri.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
               && uri.Port == expected.Port;
    }

    public static bool IsAllowed(string? origin, IEnumerable<string> allowed)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var entry in allowed)
        {
            if (Uri.TryCreate(entry, UriKind.Absolute, out var allowedUri)
                && string.Equals(uri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase)
                && uri.Port == allowedUri.Port
                && uri.AbsolutePath == "/")
            {
                return true;
            }
        }

        return false;
    }
}
