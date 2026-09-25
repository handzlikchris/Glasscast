namespace GlassesRemote.Server.Desktop;

/// <summary>
/// The monitor region the active session is casting, so the PC can draw it on screen
/// (the tray's cast-area frame). Null while no session is active.
/// </summary>
public sealed class CastArea
{
    private readonly object _gate = new();
    private CaptureRegion? _current;

    /// <summary>Raised on the caller's thread whenever the area changes; null when the session ends.</summary>
    public event Action<CaptureRegion?>? Changed;

    public CaptureRegion? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Set(CaptureRegion? region)
    {
        lock (_gate)
        {
            if (Equals(_current, region))
            {
                return;
            }

            _current = region;
        }

        Changed?.Invoke(region);
    }
}
