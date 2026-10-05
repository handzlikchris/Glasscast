using System.Net;
using GlassesRemote.Server.Alerts;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Hosting;

/// <summary>
/// The web side both servers share (the PC's and the headless relay's): <c>Web:*</c> options,
/// which hosts and origins are let in, trusting only a proxy on this machine for the client's
/// address, the strict headers, the WebSocket origin check, and the built glasses page.
/// </summary>
public static class WebHosting
{
    public static void AddGlassesWeb(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.Configure<WebOptions>(builder.Configuration.GetSection(WebOptions.SectionName));
        var isDevelopment = builder.Environment.IsDevelopment();
        services.PostConfigure<WebOptions>(web =>
        {
            // Same-origin WebSockets are a LAN-testing convenience; never honour them in production.
            if (!isDevelopment)
            {
                web.AllowSameOrigin = false;
            }

            if (web.PublicHost is { Length: > 0 } host)
            {
                web.AllowedOrigins = [.. web.AllowedOrigins, $"https://{host}"];
            }
        });
        // Runs after the host's own PostConfigure, which fills AllowedHosts from config.
        services.AddOptions<HostFilteringOptions>().PostConfigure<IOptions<WebOptions>>((filter, web) =>
        {
            if (web.Value.PublicHost is { Length: > 0 } host && !filter.AllowedHosts.Contains("*"))
            {
                filter.AllowedHosts = [.. filter.AllowedHosts, host];
            }
        });

        // Caddy on the same machine is the only proxy we trust for the client's real IP.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
            o.ForwardLimit = 1;
        });

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 16 * 1024;
        });
    }

    /// <summary>The pipeline in front of the endpoints: proxy headers, strict headers, WebSockets, the glasses page.</summary>
    public static void UseGlassesWeb(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<WebOptions>>().Value.AllowSameOrigin)
        {
            app.Logger.LogWarning("Web:AllowSameOrigin is on (LAN testing): any page served by this host may open the sockets");
        }

        app.UseForwardedHeaders();
        app.UseSecurityHeaders();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        UseClientFiles(app);
    }

    /// <summary>
    /// <c>/features</c>: what this server offers the glasses page, so a relay-only server's page
    /// goes straight to Phone.
    /// </summary>
    public static void MapFeatures(this WebApplication app, bool pc, bool phone) =>
        app.MapGet("/features", () => Results.Json(new { pc, phone }));

    /// <summary>WebSocket upgrade and exact Origin match; a bad Origin is refused before accepting.</summary>
    public static async Task<bool> AcceptGuardAsync(HttpContext context, WebOptions web, AlertLog alerts)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();
        var allowed = OriginPolicy.IsAllowed(origin, web.AllowedOrigins)
                      || (web.AllowSameOrigin && OriginPolicy.IsSameOrigin(origin, context.Request));
        if (!allowed)
        {
            alerts.Raise(AlertKind.BadOrigin, RemoteAddress(context),
                $"WebSocket refused for origin '{Truncate(origin, 100)}'");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.CompleteAsync();
            return false;
        }

        return true;
    }

    public static IPAddress RemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress ?? IPAddress.None;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static void UseClientFiles(WebApplication app)
    {
        var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;
        var root = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, web.ClientRoot));
        if (!Directory.Exists(root))
        {
            app.Logger.LogWarning("Client files not found at {Root}; build client-web first", root);
            return;
        }

        var files = new PhysicalFileProvider(root);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = ClientCaching.For(ctx.Context.Request.Path),
        });
    }
}
