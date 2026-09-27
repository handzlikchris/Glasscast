using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Ui;

namespace GlassesRemote.Server;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything touches the screen: sets PerMonitorV2 DPI awareness,
        // so capture bounds and SendInput coordinates are physical pixels.
        ApplicationConfiguration.Initialize();

        var app = ServerApp.Create(args);
        app.Start();

        // The web host runs on thread-pool threads; this STA thread runs the tray,
        // approve popup and hotkey. Exiting either one shuts down both.
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        using var tray = new TrayApp(
            app.Services.GetRequiredService<PairingCoordinator>(),
            app.Services.GetRequiredService<AlertLog>(),
            app.Services.GetRequiredService<CastArea>(),
            app.Services.GetRequiredService<CompanionRegistry>(),
            requestShutdown: lifetime.StopApplication);
        using var stopping = lifetime.ApplicationStopping.Register(tray.RequestExit);

        Application.Run(tray);

        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
