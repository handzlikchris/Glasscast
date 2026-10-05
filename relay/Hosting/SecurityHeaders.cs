namespace GlassesRemote.Server.Hosting;

/// <summary>
/// Strict headers on every response. The CSP allows only this origin's own
/// scripts and styles: no inline script, no eval, no third parties, no framing.
/// Media comes from the WebRTC track (blob:/MediaStream), sockets from this host.
/// HSTS is added by Caddy, which terminates TLS.
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            var host = context.Request.Host.Value;

            headers.ContentSecurityPolicy =
                "default-src 'none'; " +
                "script-src 'self'; " +
                "style-src 'self'; " +
                "img-src 'self' data:; " +
                "font-src 'self'; " +
                $"connect-src 'self' wss://{host}; " +
                "media-src 'self' blob:; " +
                "manifest-src 'self'; " +
                "base-uri 'none'; " +
                "form-action 'none'; " +
                "frame-ancestors 'none'";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            await next();
        });
}
