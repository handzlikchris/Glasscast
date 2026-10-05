using System.Net;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Sessions;
using GlassesRemote.Server.Windows;
using Microsoft.AspNetCore.HostFiltering;
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
        services.PostConfigure<PairingOptions>(pairing =>
        {
            // Tests and the e2e harness point this at a temp file.
            if (string.IsNullOrEmpty(pairing.DeviceGrantFile))
            {
                pairing.DeviceGrantFile = PairingOptions.DefaultDeviceGrantFile;
            }
        });
        services.Configure<ControlSessionOptions>(config.GetSection(ControlSessionOptions.SectionName));
        services.Configure<MediaOptions>(config.GetSection(MediaOptions.SectionName));
        services.Configure<AudioOptions>(config.GetSection(AudioOptions.SectionName));
        services.Configure<AppShortcutOptions>(config.GetSection(AppShortcutOptions.SectionName));
        services.Configure<CompanionOptions>(config.GetSection(CompanionOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AlertLog>();
        services.AddSingleton<PairingCoordinator>();
        services.AddSingleton(sp => new RegionStore(
            config["Desktop:RegionFile"] is { Length: > 0 } path ? path : RegionStore.DefaultPath,
            sp.GetRequiredService<ILogger<RegionStore>>()));

        services.AddSingleton(sp => new StatsLog(
            config["Diagnostics:StatsDirectory"] is { Length: > 0 } dir ? dir : StatsLog.DefaultDirectory,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<StatsLog>>()));
        services.AddSingleton<CastArea>();
        services.AddSingleton<IScreen, WindowsScreen>();
        services.AddSingleton<ICaptureSource, GdiCaptureSource>();
        services.AddSingleton<IInputInjector, Win32InputInjector>();
        services.AddSingleton<IWindowSwitcher, Win32WindowSwitcher>();
        services.AddSingleton<IKeepAwake, WindowsKeepAwake>();
        services.AddSingleton<IFrameEncoderFactory, FrameEncoderFactory>();
        services.AddSingleton<IAudioCaptureFactory, LoopbackAudioCaptureFactory>();
        services.AddSingleton<IMediaPeerFactory, MediaPeerFactory>();
        services.AddSingleton<FramePump>();
        services.AddSingleton<SessionServices>();
        services.AddSingleton<CompanionRegistry>();
        services.AddSingleton<PhoneServices>();

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

        WarnIfMultiHomed(app);

        app.UseForwardedHeaders();
        app.UseSecurityHeaders();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        UseClientFiles(app);
        app.MapGlassesEndpoints();
        app.MapCompanionEndpoint();

        return app;
    }

    /// <summary>
    /// With several adapters on the LAN, UDP replies may leave through a different one than
    /// the router forwards to, and WebRTC never connects. Media:BindAddress fixes that.
    /// </summary>
    private static void WarnIfMultiHomed(WebApplication app)
    {
        var media = app.Services.GetRequiredService<IOptions<MediaOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(media.BindAddress))
        {
            return;
        }

        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToList();

        if (addresses.Count > 1)
        {
            app.Logger.LogWarning(
                "Several network adapters have a default gateway ({Addresses}). Set Media:BindAddress to the one the " +
                "router forwards UDP {Port} to, or video may never connect from outside", string.Join(", ", addresses), media.MediaPort);
        }
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
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = ClientCaching.For(ctx.Context.Request.Path),
        });
    }
}
