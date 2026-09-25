namespace GlassesRemote.Server.Alerts;

public sealed record AlertNotice(string Title, string Text);

/// <summary>
/// Turns a stream of alerts into at most one desktop notification per interval.
/// Alerts arriving in between are counted and reported in the next notice
/// (either the next alert after the interval, or <see cref="Flush"/>), so a
/// scan can't flood the desktop but is never silently dropped.
/// </summary>
public sealed class AlertThrottle
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private DateTimeOffset _lastNotice = DateTimeOffset.MinValue;
    private int _suppressed;
    private Alert? _latestSuppressed;

    public AlertThrottle(TimeProvider time, TimeSpan interval)
    {
        _time = time;
        _interval = interval;
    }

    /// <summary>Returns a notice to show now, or null if it was folded into a later one.</summary>
    public AlertNotice? Record(Alert alert)
    {
        var now = _time.GetUtcNow();
        if (now - _lastNotice < _interval)
        {
            _suppressed++;
            _latestSuppressed = alert;
            return null;
        }

        var notice = Build(alert, _suppressed);
        _lastNotice = now;
        _suppressed = 0;
        _latestSuppressed = null;
        return notice;
    }

    /// <summary>Call periodically: emits a summary of suppressed alerts once the interval has passed.</summary>
    public AlertNotice? Flush()
    {
        var now = _time.GetUtcNow();
        if (_suppressed == 0 || _latestSuppressed is null || now - _lastNotice < _interval)
        {
            return null;
        }

        var notice = Build(_latestSuppressed, _suppressed - 1);
        _lastNotice = now;
        _suppressed = 0;
        _latestSuppressed = null;
        return notice;
    }

    private static AlertNotice Build(Alert alert, int others)
    {
        var from = alert.RemoteAddress is null ? "" : $" from {alert.RemoteAddress}";
        var more = others > 0 ? $" (+{others} more)" : "";
        return new AlertNotice(
            $"Glasses remote: {Describe(alert.Kind)}{more}",
            $"{alert.Detail}{from}. Open Recent alerts in the tray menu for details.");
    }

    private static string Describe(AlertKind kind) => kind switch
    {
        AlertKind.PairingRejected => "pairing rejected",
        AlertKind.PairingTimedOut => "pairing request not approved",
        AlertKind.PairingWhileBusy => "pairing attempt while busy",
        AlertKind.PairingRateLimited => "too many pairing attempts",
        AlertKind.AuthenticationFailed => "failed session login",
        AlertKind.AuthenticationTimedOut => "silent connection dropped",
        AlertKind.BadOrigin => "connection from another site",
        AlertKind.ProtocolViolation => "invalid messages",
        AlertKind.MessageRateLimited => "message flood",
        AlertKind.DeviceTokenReused => "old device token reused, device forgotten",
        _ => "unexpected connection",
    };
}
