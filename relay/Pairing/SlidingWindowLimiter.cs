namespace GlassesRemote.Server.Pairing;

/// <summary>
/// Counts events per key over a sliding time window. Small and deterministic
/// (driven by <see cref="TimeProvider"/>) so limits are easy to test.
/// Not thread-safe on its own; callers hold their own lock.
/// </summary>
public sealed class SlidingWindowLimiter
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly int _limit;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _events = new();

    public SlidingWindowLimiter(TimeProvider time, TimeSpan window, int limit)
    {
        _time = time;
        _window = window;
        _limit = limit;
    }

    /// <summary>Records an event for <paramref name="key"/> if under the limit.</summary>
    public bool TryAcquire(string key)
    {
        var now = _time.GetUtcNow();
        if (!_events.TryGetValue(key, out var queue))
        {
            queue = new Queue<DateTimeOffset>();
            _events[key] = queue;
        }

        while (queue.Count > 0 && now - queue.Peek() >= _window)
        {
            queue.Dequeue();
        }

        if (queue.Count >= _limit)
        {
            return false;
        }

        queue.Enqueue(now);
        PruneIdleKeys(now);
        return true;
    }

    private void PruneIdleKeys(DateTimeOffset now)
    {
        // Keep the dictionary from growing without bound under a scan from many IPs.
        if (_events.Count < 1024)
        {
            return;
        }

        foreach (var key in _events.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() >= _window)
                     .Select(kv => kv.Key).ToList())
        {
            _events.Remove(key);
        }
    }
}
