namespace GlassesRemote.Server.Desktop;

/// <summary>
/// The monitor region the active session is casting, so the PC can draw it on screen
/// (the tray's cast-area frame). Null while no session is active. Also whether the PC's sound
/// goes to the glasses, which the session banner says.
/// </summary>
public sealed class CastArea
{
    private readonly object _gate = new();
    private CaptureRegion? _current;
    private bool _audio;

    /// <summary>Raised on the caller's thread when the sound starts or stops going to the glasses.</summary>
    public event Action<bool>? AudioChanged;

    public bool Audio
    {
        get
        {
            lock (_gate)
            {
                return _audio;
            }
        }
    }

    public void SetAudio(bool on)
    {
        lock (_gate)
        {
            if (_audio == on)
            {
                return;
            }

            _audio = on;
        }

        AudioChanged?.Invoke(on);
    }

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
