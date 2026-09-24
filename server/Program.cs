using GlassesRemote.Server.Hosting;

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
        app.Run();
    }
}
