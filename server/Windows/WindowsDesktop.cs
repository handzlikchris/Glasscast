using GlassesRemote.Server.Desktop;
using static GlassesRemote.Server.Windows.NativeMethods;

namespace GlassesRemote.Server.Windows;

public sealed class WindowsScreen : IScreen
{
    // Physical pixels: the process is PerMonitorV2 DPI aware (set in Program.Main).
    public PixelSize PrimarySize
    {
        get
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            return new PixelSize(bounds.Width, bounds.Height);
        }
    }
}

/// <summary>
/// Keeps the system and display awake while at least one holder exists.
/// SetThreadExecutionState is per-thread, so a dedicated thread owns the request.
/// </summary>
public sealed class WindowsKeepAwake : IKeepAwake, IDisposable
{
    private readonly ILogger<WindowsKeepAwake> _logger;
    private readonly object _gate = new();
    private int _holders;
    private ManualResetEventSlim? _release;

    public WindowsKeepAwake(ILogger<WindowsKeepAwake> logger)
    {
        _logger = logger;
    }

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_holders++ == 0)
            {
                var release = new ManualResetEventSlim();
                _release = release;
                var thread = new Thread(() =>
                {
                    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
                    release.Wait();
                    SetThreadExecutionState(ES_CONTINUOUS);
                    release.Dispose();
                })
                {
                    IsBackground = true,
                    Name = "KeepAwake",
                };
                thread.Start();
                _logger.LogInformation("Keeping the PC awake for the session");
            }
        }

        return new Holder(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holders == 0)
            {
                _release?.Set();
                _release = null;
                _logger.LogInformation("Released keep-awake");
            }
        }
    }

    public void Dispose() => _release?.Set();

    private sealed class Holder(WindowsKeepAwake owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
