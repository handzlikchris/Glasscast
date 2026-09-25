// DEV-ONLY: runs the real server pipeline (Kestrel, WebSockets, pairing, SIPSorcery,
// VP8, GDI capture) with two changes so a headless browser can drive it unattended:
//   1. pairing requests are approved automatically;
//   2. input and app switches are recorded, never applied to the real desktop.
// It listens on 127.0.0.1:5081 only and must never be deployed.
using System.Collections.Concurrent;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Protocol;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

var repoRoot = FindRepoRoot();
var recorder = new RecordingInput();

var app = ServerApp.Create(args, builder =>
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Urls"] = "http://127.0.0.1:5081",
        ["Web:AllowedOrigins:0"] = "http://127.0.0.1:5081",
        ["Web:ClientRoot"] = Path.Combine(repoRoot, "client-web", "dist"),
        ["Media:IncludeLanCandidates"] = "true",
        ["Media:PublicIp"] = "",
        ["Desktop:RegionFile"] = Path.Combine(Path.GetTempPath(), "glasses-e2e-region.json"),
        ["Apps:Shortcuts:0:Name"] = "Claude",
        ["Apps:Shortcuts:0:Title"] = "herdr",
        ["Apps:Shortcuts:1:Name"] = "Browser",
        ["Apps:Shortcuts:1:Process"] = "chrome",
    });
    builder.Services.AddSingleton<IInputInjector>(recorder);
    // Never move real windows from the harness: record the switch instead.
    builder.Services.AddSingleton<IWindowSwitcher>(new RecordingSwitcher(recorder));
    builder.Services.AddSingleton<IKeepAwake, NoKeepAwake>();
});

var coordinator = app.Services.GetRequiredService<PairingCoordinator>();
coordinator.RequestOpened += request =>
{
    app.Logger.LogWarning("E2E HARNESS: auto-approving pairing request {Code}", request.Code);
    Task.Run(() => coordinator.Approve(request.Id));
};

// Lets the browser script check what the "desktop" received.
app.MapGet("/__harness/input", () => recorder.Actions.ToArray());
app.MapGet("/__harness/session", () => coordinator.ActiveSession);
app.MapGet("/__harness/region", () => app.Services.GetRequiredService<RegionStore>().Load());

app.Run();

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GlassesRemote.sln")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repo");
}

sealed class RecordingInput : IInputInjector
{
    public ConcurrentQueue<string> Actions { get; } = new();

    public void MoveTo(int x, int y) => Actions.Enqueue($"move {x},{y}");

    public void Click(MouseButton button) => Actions.Enqueue($"click {button}");

    public void Wheel(int delta) => Actions.Enqueue($"wheel {delta}");

    public void TypeText(string text) => Actions.Enqueue($"type {text}");

    public void Press(KeyCommand key) => Actions.Enqueue($"key {key}");
}

sealed class NoKeepAwake : IKeepAwake
{
    public IDisposable Acquire() => new Noop();

    private sealed class Noop : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

sealed class RecordingSwitcher(RecordingInput recorder) : IWindowSwitcher
{
    public AppSwitchResult Switch(AppShortcut app, CaptureRegion area)
    {
        recorder.Actions.Enqueue($"switch {app.Name} {area.X},{area.Y} {area.Width}x{area.Height}");
        return AppSwitchResult.Switched;
    }
}
