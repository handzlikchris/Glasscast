using System.Net;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Sessions;
using GlassesRemote.Server.Windows;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Hosting;

/// <summary>Builds the web host. Program and the integration tests share this.</summary>
public static class ServerApp
{
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var config = builder.Configuration;

        // Machine-specific values (e.g. Media:PublicIp) live in a git-ignored file.
        config.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
        var services = builder.Services;

        services.Configure<PairingOptions>(config.GetSection(PairingOptions.SectionName));
        services.Configure<WebOptions>(config.GetSection(WebOptions.SectionName));
        var isDevelopment = builder.Environment.IsDevelopment();
        services.PostConfigure<WebOptions>(web =>
        {
            // Same-origin WebSockets are a LAN-testing convenience; never honour them in production.
            if (!isDevelopment)
            {
                web.AllowSameOrigin = false;
            }
        });
        services.Configure<ControlSessionOptions>(config.GetSection(ControlSessionOptions.SectionName));
        services.Configure<MediaOptions>(config.GetSection(MediaOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AlertLog>();
        services.AddSingleton<PairingCoordinator>();
        services.AddSingleton(sp => new RegionStore(
            config["Desktop:RegionFile"] is { Length: > 0 } path ? path : RegionStore.DefaultPath,
            sp.GetRequiredService<ILogger<RegionStore>>()));

        services.AddSingleton<IScreen, WindowsScreen>();
        services.AddSingleton<ICaptureSource, GdiCaptureSource>();
        services.AddSingleton<IInputInjector, Win32InputInjector>();
        services.AddSingleton<IKeepAwake, WindowsKeepAwake>();
        services.AddSingleton<IFrameEncoderFactory, FrameEncoderFactory>();
        services.AddSingleton<IMediaPeerFactory, MediaPeerFactory>();
        services.AddSingleton<FramePump>();
        services.AddSingleton<SessionServices>();

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

        configure?.Invoke(builder);

        var app = builder.Build();
        SIPSorcery.LogFactory.Set(app.Services.GetRequiredService<ILoggerFactory>());

        if (app.Services.GetRequiredService<IOptions<WebOptions>>().Value.AllowSameOrigin)
        {
            app.Logger.LogWarning("Web:AllowSameOrigin is on (LAN testing): any page served by this host may open the sockets");
        }

        app.UseForwardedHeaders();
        app.UseSecurityHeaders();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        UseClientFiles(app);
        app.MapGlassesEndpoints();

        return app;
    }

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
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    }
}
