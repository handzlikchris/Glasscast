// DEV-ONLY: runs the real server pipeline (Kestrel, WebSockets, pairing, SIPSorcery,
// VP8, GDI capture) with two changes so a headless browser can drive it unattended:
//   1. pairing requests are approved automatically;
//   2. input and app switches are recorded, never applied to the real desktop.
// It can also lose the start of the next session's video stream (POST /__harness/lose-stream-start)
// or one packet of the current one (POST /__harness/lose-packet, resent when NACKed;
// /__harness/lose-packet-for-good, never resent).
// It listens on 127.0.0.1:5081 only and must never be deployed.
using System.Collections.Concurrent;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Media;
using Microsoft.Extensions.Options;
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
        // Keyframes mostly on request, so the lost-stream-start check sees them answered.
        ["Media:KeyframeIntervalSeconds"] = "30",
        ["Media:PublicIp"] = "",
        ["Desktop:RegionFile"] = Path.Combine(Path.GetTempPath(), "glasses-e2e-region.json"),
        ["Diagnostics:StatsDirectory"] = Path.Combine(Path.GetTempPath(), "glasses-e2e-stats"),
        ["Pairing:DeviceGrantFile"] = Path.Combine(Path.GetTempPath(), "glasses-e2e-device-grant.json"),
        ["Apps:Shortcuts:0:Name"] = "Claude",
        ["Apps:Shortcuts:0:Title"] = "herdr",
        ["Apps:Shortcuts:1:Name"] = "Browser",
        ["Apps:Shortcuts:1:Process"] = "chrome",
    });
    builder.Services.AddSingleton<IInputInjector>(recorder);
    // Never move real windows from the harness: record the switch instead.
    builder.Services.AddSingleton<IWindowSwitcher>(new RecordingSwitcher(recorder));
    builder.Services.AddSingleton<IKeepAwake, NoKeepAwake>();
    builder.Services.AddSingleton<HarnessPeers>();
    // A steady tone instead of the PC's real sound: the checks need audio that's always there.
    builder.Services.AddSingleton<IAudioCaptureFactory, ToneCaptureFactory>();
    builder.Services.AddSingleton<IMediaPeerFactory>(sp => sp.GetRequiredService<HarnessPeers>());
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
// Ends the session like the tray's "End session", to reach the client's "Session ended" screen.
app.MapPost("/__harness/terminate", () => coordinator.TerminateActiveSession());
// The next session's first frames (its first keyframe included) never reach the browser.
app.MapPost("/__harness/lose-stream-start", () => app.Services.GetRequiredService<HarnessPeers>().DropStartOfNext = true);
// The current stream's next packet doesn't reach the browser (but is resent if NACKed).
app.MapPost("/__harness/lose-packet", () => app.Services.GetRequiredService<HarnessPeers>().LoseOnePacket(forGood: false));
// The current stream's next packet is lost for good: only a keyframe repairs the picture.
app.MapPost("/__harness/lose-packet-for-good", () => app.Services.GetRequiredService<HarnessPeers>().LoseOnePacket(forGood: true));

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

/// <summary>The real media peers; can make the next one lose the start of its stream.</summary>
sealed class HarnessPeers(IOptions<MediaOptions> options, IOptions<AudioOptions> audio, ILoggerFactory loggers) : IMediaPeerFactory
{
    private readonly MediaPeerFactory _real = new(options, audio, loggers);

    private SipsorceryMediaPeer? _latest;

    public bool DropStartOfNext { get; set; }

    public void LoseOnePacket(bool forGood) => _latest?.LoseOnePacket(forGood);

    public IMediaPeer Create(string codec)
    {
        var peer = _real.Create(codec);
        _latest = peer as SipsorceryMediaPeer;
        // Every stream crosses a sequence-number wrap early, so the checks after it (resends
        // included) run in the second SRTP rollover epoch.
        _latest?.StartNearSequenceWrap();
        if (DropStartOfNext && peer is SipsorceryMediaPeer real)
        {
            DropStartOfNext = false;
            real.DropFirstFrames(5);
        }
        return peer;
    }
}

/// <summary>A 440 Hz tone, beeping on and off each second (so DTX gets exercised), at wall-clock pace like WASAPI.</summary>
sealed class ToneCaptureFactory : IAudioCaptureFactory
{
    public IAudioCapture? Start() => new Tone();

    private sealed class Tone : IAudioCapture
    {
        private const int Piece = AudioFormat48k.SampleRate / 100;
        private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
        private readonly float[] _buffer = new float[Piece * 2];
        private long _delivered;

        public bool IsStale => false;

        public void ReadInto(AudioFifo fifo)
        {
            var due = System.Diagnostics.Stopwatch.GetElapsedTime(_start).Ticks * AudioFormat48k.SampleRate / TimeSpan.TicksPerSecond;
            while (_delivered + Piece <= due)
            {
                var on = _delivered / AudioFormat48k.SampleRate % 2 == 0;
                for (var i = 0; i < Piece; i++)
                {
                    var t = (_delivered + i) / (float)AudioFormat48k.SampleRate;
                    _buffer[2 * i] = _buffer[2 * i + 1] = on ? 0.2f * MathF.Sin(2 * MathF.PI * 440 * t) : 0f;
                }
                fifo.Write(_buffer);
                _delivered += Piece;
            }
        }

        public void Dispose()
        {
        }
    }
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
