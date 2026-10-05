namespace GlassesRemote.Server.Hosting;

/// <summary>
/// Cache rules for the built client. The page itself must be revalidated on every load:
/// the glasses' Restart reloads from cache, so a heuristically cached index.html keeps an
/// old build running and clearing it means removing the web app. Vite's hashed assets
/// never change under the same name, so they can be cached for good.
/// </summary>
public static class ClientCaching
{
    public const string Revalidate = "no-cache";
    public const string Immutable = "public, max-age=31536000, immutable";

    public static string For(PathString path) =>
        path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase) ? Immutable : Revalidate;
}
