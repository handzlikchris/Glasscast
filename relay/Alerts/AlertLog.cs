using System.Net;

namespace GlassesRemote.Server.Alerts;

public enum AlertKind
{
    PairingRejected,
    PairingTimedOut,
    PairingWhileBusy,
    PairingRateLimited,
    AuthenticationFailed,
    AuthenticationTimedOut,
    BadOrigin,
    ProtocolViolation,
    MessageRateLimited,
    DeviceTokenReused,
}

public sealed record Alert(DateTimeOffset At, AlertKind Kind, string? RemoteAddress, string Detail);

/// <summary>
/// Signs that someone other than you may be probing the endpoint. Keeps the most
/// recent alerts for the approve window's list and raises <see cref="Raised"/>
/// for Windows notifications (the UI throttles those).
/// Details never contain secrets: no tokens, no message payloads.
/// </summary>
public sealed class AlertLog
{
    private const int Capacity = 200;

    private readonly TimeProvider _time;
    private readonly ILogger<AlertLog> _logger;
    private readonly LinkedList<Alert> _recent = new();
    private readonly object _gate = new();

    public AlertLog(TimeProvider time, ILogger<AlertLog> logger)
    {
        _time = time;
        _logger = logger;
    }

    public event Action<Alert>? Raised;

    public void Raise(AlertKind kind, IPAddress? remote, string detail)
    {
        var alert = new Alert(_time.GetUtcNow(), kind, remote?.ToString(), detail);
        lock (_gate)
        {
            _recent.AddFirst(alert);
            while (_recent.Count > Capacity)
            {
                _recent.RemoveLast();
            }
        }

        _logger.LogWarning("Alert {Kind} from {Remote}: {Detail}", kind, alert.RemoteAddress ?? "?", detail);
        Raised?.Invoke(alert);
    }

    public IReadOnlyList<Alert> Recent()
    {
        lock (_gate)
        {
            return _recent.ToList();
        }
    }
}
