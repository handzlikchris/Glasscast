using System.Text.Json;

namespace GlassesRemote.Server.Media;

/// <summary>
/// Appends media measurements to a daily JSON Lines file (<c>stats-yyyy-MM-dd.jsonl</c>) so a
/// session's latency can be read back after testing on the glasses: the glasses' own figures
/// (their <c>stats</c> message), the PC's frame pump figures, and session events such as app
/// switches. Only numbers and fixed names go in: never typed text, tokens or SDP.
/// </summary>
public sealed class StatsLog
{
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly ILogger<StatsLog> _logger;
    private readonly Lock _lock = new();
    private bool _warned;

    public StatsLog(string directory, TimeProvider time, ILogger<StatsLog> logger)
    {
        _directory = directory;
        _time = time;
        _logger = logger;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassesRemote", "stats");

    public string Directory => _directory;

    /// <param name="kind">"glasses", "pc" or "event".</param>
    public void Write(string session, string kind, IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var now = _time.GetLocalNow();
        var line = new Dictionary<string, object?>
        {
            ["t"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["session"] = session,
            ["kind"] = kind,
        };
        foreach (var (key, value) in fields)
        {
            line[key] = value;
        }

        var json = JsonSerializer.Serialize(line) + "\n";
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"stats-{now:yyyy-MM-dd}.jsonl"), json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!_warned)
                {
                    _logger.LogWarning(ex, "Could not write media stats to {Directory}", _directory);
                    _warned = true;
                }
            }
        }
    }
}
