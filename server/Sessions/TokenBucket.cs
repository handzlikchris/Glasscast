namespace GlassesRemote.Server.Sessions;

/// <summary>Classic token bucket on an injectable clock. Not thread-safe; one per session.</summary>
public sealed class TokenBucket
{
    private readonly TimeProvider _time;
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private double _tokens;
    private long _lastTimestamp;

    public TokenBucket(TimeProvider time, double ratePerSecond, double capacity)
    {
        _time = time;
        _ratePerSecond = ratePerSecond;
        _capacity = capacity;
        _tokens = capacity;
        _lastTimestamp = time.GetTimestamp();
    }

    public bool TryTake(double cost = 1)
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;
        _tokens = Math.Min(_capacity, _tokens + elapsed * _ratePerSecond);

        if (_tokens < cost)
        {
            return false;
        }

        _tokens -= cost;
        return true;
    }
}
